using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Npgsql;
using OpsApi.Endpoints;
using OpsApi.Infrastructure;
using OpsApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

// PostgreSQL — ops.audit_events için ayrı şema (yonetim'den bağımsız)
var connStr = builder.Configuration.GetConnectionString("PlatformDb")!;
var dataSource = NpgsqlDataSource.Create(connStr);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<OpsAuditService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var authority = builder.Configuration["Keycloak:Authority"]!;
        options.Authority = authority;
        options.MetadataAddress = builder.Configuration["Keycloak:MetadataAddress"]
            ?? $"{authority}/.well-known/openid-configuration";
        options.RequireHttpsMetadata = builder.Configuration.GetValue<bool>("Keycloak:RequireHttpsMetadata");
        options.MapInboundClaims = false;
        options.BackchannelHttpHandler = new KeycloakBackchannelHandler();
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateAudience = false,
            ValidIssuers = [authority, "http://keycloak:8080/realms/platform"],
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (string.IsNullOrEmpty(ctx.Token))
                    ctx.Token = ctx.Request.Cookies["at"];
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ops.read",    p => p.RequireAssertion(ctx => HasOpsRole(ctx.User, "ops.read", "ops.execute", "ops.admin")));
    options.AddPolicy("ops.execute", p => p.RequireAssertion(ctx => HasOpsRole(ctx.User, "ops.execute", "ops.admin")));
    options.AddPolicy("ops.admin",   p => p.RequireAssertion(ctx => HasOpsRole(ctx.User, "ops.admin")));
});

// 403 → 404 dönüşümü: ops rolü olmayan authenticated kullanıcıya /ops/* URL varlığını gizle
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, OpsForbidToNotFoundHandler>();

var app = builder.Build();

// ── Ops Audit Middleware ──────────────────────────────────────────────────────
// UseAuthentication/UseAuthorization'dan ÖNCE — 401/404 dahil tüm /ops/* loglanır
app.Use(async (ctx, next) =>
{
    // Correlation ID — istek boyunca takip edilir, response header'a da eklenir
    var correlationId = Guid.NewGuid().ToString("N")[..16];
    ctx.Response.Headers["X-Correlation-Id"] = correlationId;

    var sw = Stopwatch.StartNew();
    await next();
    sw.Stop();

    if (!ctx.Request.Path.StartsWithSegments("/ops")) return;

    var status = ctx.Response.StatusCode;
    var actor  = ctx.User.FindFirst("preferred_username")?.Value ?? "anonymous";
    var action = OpsAuditService.MapAction(ctx.Request.Method, ctx.Request.Path.Value ?? "");

    var (result, reasonCode) = status switch
    {
        401                       => ("denied",  "no_token"),
        404 when actor == "anonymous" => ("denied", "no_token"),
        404                       => ("denied",  "ops_role_missing"),
        >= 200 and < 300          => ("success", (string?)null),
        _                         => ("error",   $"http_{status}"),
    };

    var audit = ctx.RequestServices.GetRequiredService<OpsAuditService>();
    await audit.WriteAsync(
        actor, action, result, reasonCode, correlationId,
        ctx.Connection.RemoteIpAddress?.ToString(),
        ctx.Request.Path.Value, ctx.Request.Method,
        (int)sw.ElapsedMilliseconds);
});
// ─────────────────────────────────────────────────────────────────────────────

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "OpsApi" }));
app.MapOpsEndpoints();

app.Run();

static bool HasOpsRole(System.Security.Claims.ClaimsPrincipal user, params string[] allowed)
{
    if (!user.Identity?.IsAuthenticated ?? true) return false;
    var realmAccess = user.FindFirst("realm_access")?.Value;
    if (realmAccess == null) return false;
    try
    {
        using var doc = JsonDocument.Parse(realmAccess);
        if (!doc.RootElement.TryGetProperty("roles", out var roles)) return false;
        var roleSet = roles.EnumerateArray()
            .Select(r => r.GetString())
            .Where(r => r != null)
            .ToHashSet()!;
        return allowed.Any(a => roleSet.Contains(a));
    }
    catch { return false; }
}
