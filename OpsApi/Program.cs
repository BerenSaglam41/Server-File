using Microsoft.AspNetCore.Authentication.JwtBearer;
using OpsApi.Endpoints;
using OpsApi.Infrastructure;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

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
            ValidIssuers = new[] { authority, "http://keycloak:8080/realms/platform" },
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
    // Okuma: ops.read veya daha yüksek bir rol
    options.AddPolicy("ops.read", policy =>
        policy.RequireAssertion(ctx => HasOpsRole(ctx.User, "ops.read", "ops.execute", "ops.admin")));

    // Güvenli write işlemleri (backup tetikleme): ops.execute veya ops.admin
    options.AddPolicy("ops.execute", policy =>
        policy.RequireAssertion(ctx => HasOpsRole(ctx.User, "ops.execute", "ops.admin")));

    // Yıkıcı işlemler (restore, backup silme): yalnızca ops.admin
    options.AddPolicy("ops.admin", policy =>
        policy.RequireAssertion(ctx => HasOpsRole(ctx.User, "ops.admin")));
});

var app = builder.Build();

// ── Ops Audit Middleware ─────────────────────────────────────────────────────
// UseAuthentication/UseAuthorization'dan ÖNCE konumlanmalı:
// Authorization 403/401 döndüğünde short-circuit yapar ve sonraki
// middleware'leri çağırmaz. Burada olunca pipeline'ı tamamen sararız,
// 200/401/403 dahil tüm /ops/* istekleri loglanır.
var auditRoot = builder.Configuration["AUDIT_ROOT"] ?? "/ops/audit";
Directory.CreateDirectory(auditRoot);
app.Use(async (ctx, next) =>
{
    await next();
    if (!ctx.Request.Path.StartsWithSegments("/ops")) return;

    var entry = new
    {
        timestamp = DateTime.UtcNow.ToString("O"),
        method    = ctx.Request.Method,
        path      = ctx.Request.Path.Value,
        status    = ctx.Response.StatusCode,
        user      = ctx.User.FindFirst("preferred_username")?.Value ?? "anonymous",
        sub       = ctx.User.FindFirst("sub")?.Value,
        ip        = ctx.Connection.RemoteIpAddress?.ToString(),
    };

    try
    {
        var line = JsonSerializer.Serialize(entry) + "\n";
        var auditFile = Path.Combine(auditRoot, "ops-audit.jsonl");
        await File.AppendAllTextAsync(auditFile, line);
    }
    catch { /* audit yazılamazsa istek kesilmesin */ }
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
            .ToHashSet();
        return allowed.Any(a => roleSet.Contains(a));
    }
    catch { return false; }
}
