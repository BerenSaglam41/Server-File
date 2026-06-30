using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography.X509Certificates;
using FileServiceApi.Data;
using FileServiceApi.Infrastructure;
using FileServiceApi.Services;
using FileServiceApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// ── mTLS: Kestrel HTTPS + istemci sertifika doğrulaması ────────────────────
var serverCertPath = builder.Configuration["Mtls:ServerCertPath"];
var serverKeyPath  = builder.Configuration["Mtls:ServerKeyPath"];
var caCertPath     = builder.Configuration["Mtls:CaCertPath"];
var allowedCNs     = (builder.Configuration.GetSection("Mtls:AllowedClientCNs").Get<string[]>()
                      ?? ["yonetimapi", "filoapi"])
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);

if (!string.IsNullOrEmpty(serverCertPath))
{
    var caCert = new X509Certificate2(caCertPath!);

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(8080, endpoint =>
        {
            endpoint.UseHttps(https =>
            {
                https.ServerCertificate = X509Certificate2.CreateFromPemFile(serverCertPath, serverKeyPath!);
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                // CN izin listesinde olmalı + CA tarafından imzalanmış olmalı
                https.ClientCertificateValidation = (cert, chain, _) =>
                {
                    var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
                    if (!allowedCNs.Contains(cn)) return false;
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(caCert);
                    return chain.Build(cert);
                };
            });
        });
    });
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PlatformDb")));

builder.Services.AddScoped<AuditService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var authority = builder.Configuration["Keycloak:Authority"]!;
        options.Authority = authority;
        // Docker'da JWKS keycloak:8080'den çekilir ama issuer localhost:8080 kalır.
        options.MetadataAddress = builder.Configuration["Keycloak:MetadataAddress"]
            ?? $"{authority}/.well-known/openid-configuration";
        options.RequireHttpsMetadata = builder.Configuration.GetValue<bool>("Keycloak:RequireHttpsMetadata");
        // KC_HOSTNAME=localhost → jwks_uri localhost:8080 döner ama container içinden ulaşılamaz.
        options.BackchannelHttpHandler = new KeycloakBackchannelHandler();
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateAudience = false,
            ValidIssuers = new[] { authority, "http://keycloak:8080/realms/platform" },
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapFileEndpoints();

app.MapGet("/health", async (IConfiguration config, AppDbContext db) =>
{
    var storageRoot = config["FileStorage:ReadPath"]!;
    var probePath = Path.Combine(storageRoot, ".probe");

    var storageOk = false;
    string? storageReason = null;
    try
    {
        await File.ReadAllTextAsync(probePath);
        storageOk = true;
    }
    catch (Exception ex)
    {
        storageReason = ex is FileNotFoundException ? "probe_not_found" : "probe_read_failed";
    }

    var dbOk = false;
    string? dbReason = null;
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
        dbOk = true;
    }
    catch
    {
        dbReason = "db_unreachable";
    }

    var healthy = storageOk && dbOk;
    var response = new
    {
        status = healthy ? "healthy" : "unhealthy",
        service = "FileServiceApi",
        checks = new
        {
            storage = storageOk
                ? (object)new { status = "healthy" }
                : new { status = "unhealthy", reason = storageReason },
            database = dbOk
                ? (object)new { status = "healthy" }
                : new { status = "unhealthy", reason = dbReason }
        }
    };

    return healthy ? Results.Ok(response) : Results.Json(response, statusCode: 503);
});

app.Run();
