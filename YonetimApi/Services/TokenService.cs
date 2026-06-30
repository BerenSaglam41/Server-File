using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace YonetimApi.Services;

public interface ITokenService
{
    Task<string> GetServiceTokenAsync();
}

public class KeycloakTokenService : ITokenService
{
    private readonly IHttpClientFactory _factory;
    private readonly string _tokenUrl;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public KeycloakTokenService(IHttpClientFactory factory, IConfiguration config)
    {
        _factory = factory;
        _tokenUrl = config["Keycloak:TokenUrl"]!;
        _clientId = config["Keycloak:ClientId"]!;
        _clientSecret = config["Keycloak:ClientSecret"]!;
    }

    public async Task<string> GetServiceTokenAsync()
    {
        // Token hâlâ geçerliyse önbellekten dön (30 saniye erken expire et)
        if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry)
            return _cachedToken;

        await _lock.WaitAsync();
        try
        {
            // Double-check: başka thread zaten yenilemiş olabilir
            if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry)
                return _cachedToken;

            var client = _factory.CreateClient();
            var resp = await client.PostAsync(_tokenUrl, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret
                }));

            resp.EnsureSuccessStatusCode();
            var token = await resp.Content.ReadFromJsonAsync<TokenResponse>()
                ?? throw new InvalidOperationException("Keycloak token response boş döndü");

            _cachedToken = token.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(token.ExpiresIn - 30);

            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
