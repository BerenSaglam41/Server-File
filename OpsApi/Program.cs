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
    options.AddPolicy("ops.read", policy =>
        policy.RequireAssertion(ctx =>
        {
            if (!ctx.User.Identity?.IsAuthenticated ?? true) return false;
            var realmAccess = ctx.User.FindFirst("realm_access")?.Value;
            if (realmAccess == null) return false;
            try
            {
                using var doc = JsonDocument.Parse(realmAccess);
                if (!doc.RootElement.TryGetProperty("roles", out var roles)) return false;
                return roles.EnumerateArray()
                    .Any(r => r.GetString() is "ops.read" or "ops.admin");
            }
            catch { return false; }
        }));
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "OpsApi" }));
app.MapOpsEndpoints();

app.Run();
