using Microsoft.AspNetCore.Authentication.JwtBearer;
using Npgsql;
using System.Security.Cryptography.X509Certificates;
using FlotaApi.Endpoints;
using FlotaApi.Infrastructure;
using FlotaApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var dataSource = NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("PlatformDb")!);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<IDomainAuditService, DomainAuditService>();

// ── mTLS: FileService HttpClient — client sertifikası + CA doğrulaması ───────
var clientCertPath = builder.Configuration["Mtls:ClientCertPath"];
var clientKeyPath  = builder.Configuration["Mtls:ClientKeyPath"];
var mtlsCaCertPath = builder.Configuration["Mtls:CaCertPath"];
bool mtlsEnabled   = !string.IsNullOrEmpty(clientCertPath);

builder.Services.AddHttpClient("FileService", client =>
{
    var baseUrl = builder.Configuration["FileService:BaseUrl"] ?? "http://localhost:5205";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    if (!mtlsEnabled)
        return new HttpClientHandler();

    var raw  = X509Certificate2.CreateFromPemFile(clientCertPath!, clientKeyPath!);
    var cert = new X509Certificate2(raw.Export(X509ContentType.Pkcs12));

    var ca = new X509Certificate2(mtlsCaCertPath!);
    var handler = new HttpClientHandler();
    handler.ClientCertificates.Add(cert);
    handler.ServerCertificateCustomValidationCallback = (_, serverCert, chain, _) =>
    {
        if (serverCert == null || chain == null) return false;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        return chain.Build(serverCert);
    };
    return handler;
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<ITokenService, KeycloakTokenService>();

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
        // BFF: token önce "at" cookie'sinden okunur; curl testleri için header da desteklenir.
        options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (string.IsNullOrEmpty(ctx.Token))
                    ctx.Token = ctx.Request.Cookies["at"];
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapVehicleEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "FlotaApi" }));

app.Run();
