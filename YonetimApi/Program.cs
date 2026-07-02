using Microsoft.AspNetCore.Authentication.JwtBearer;
using Npgsql;
using System.Security.Cryptography.X509Certificates;
using YonetimApi.Endpoints;
using YonetimApi.Infrastructure;
using YonetimApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var dataSource = NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("PlatformDb")!);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<IDomainAuditService, DomainAuditService>();
builder.Services.AddSingleton<IPermissionService, PersonnelPermissionService>();

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

    // Linux: PEM'den yükle → PKCS12'ye aktar → geri yükle (private key bağlaması için)
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

// TokenService'in Keycloak'a istek yapması için default HttpClient
builder.Services.AddHttpClient();

// YonetimApi'nin FileService'e göndereceği service token'ı önbellekli sağlar
builder.Services.AddSingleton<ITokenService, KeycloakTokenService>();

// Gelen client isteklerini doğrula: HttpOnly cookie "at" veya Authorization header
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var authority = builder.Configuration["Keycloak:Authority"]!;
        options.Authority = authority;
        options.MetadataAddress = builder.Configuration["Keycloak:MetadataAddress"]
            ?? $"{authority}/.well-known/openid-configuration";
        options.RequireHttpsMetadata = builder.Configuration.GetValue<bool>("Keycloak:RequireHttpsMetadata");
        options.MapInboundClaims = false;
        // KC_HOSTNAME=localhost → jwks_uri localhost:8080 döner ama container içinden ulaşılamaz.
        // Bu handler localhost:8080 → keycloak:8080 yönlendirir.
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
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "YonetimApi" }));
app.MapAuthEndpoints();
app.MapPersonnelEndpoints();
app.MapDownloadTicketEndpoints();

app.Run();
