using System.Text.Json;
using System.Text.Json.Serialization;

namespace YonetimApi.Endpoints;

public static class AuthEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").AllowAnonymous();
        group.MapPost("/login",   LoginAsync);
        group.MapPost("/refresh", RefreshAsync);
        group.MapPost("/logout",  LogoutAsync);
    }

    // ─── LOGIN ───────────────────────────────────────────────────────────────
    private static async Task<IResult> LoginAsync(
        HttpContext ctx,
        IConfiguration config,
        IHttpClientFactory httpFactory)
    {
        LoginRequest? body;
        try { body = await ctx.Request.ReadFromJsonAsync<LoginRequest>(); }
        catch { return Results.BadRequest(new { error = "invalid_request" }); }

        if (body is null || string.IsNullOrEmpty(body.Username) || string.IsNullOrEmpty(body.Password))
            return Results.BadRequest(new { error = "username_password_required" });

        var tokenUrl  = config["Keycloak:TokenUrl"]!;
        var clientId  = config["Keycloak:FrontendClientId"]!;

        var http = httpFactory.CreateClient();
        var kcResp = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"]  = clientId,
            ["username"]   = body.Username,
            ["password"]   = body.Password,
        }));

        if (!kcResp.IsSuccessStatusCode)
            return Results.Json(new { error = "invalid_credentials" }, statusCode: 401);

        var kc = await JsonSerializer.DeserializeAsync<KcTokenResponse>(
            await kcResp.Content.ReadAsStreamAsync(), JsonOpts);
        if (kc?.AccessToken is null) return Results.StatusCode(502);

        SetCookies(ctx.Response, kc);
        var user = DecodeJwtPayload(kc.AccessToken);
        return Results.Ok(BuildResponse(user, kc.ExpiresIn));
    }

    // ─── REFRESH ─────────────────────────────────────────────────────────────
    private static async Task<IResult> RefreshAsync(
        HttpContext ctx,
        IConfiguration config,
        IHttpClientFactory httpFactory)
    {
        var rt = ctx.Request.Cookies["rt"];
        if (string.IsNullOrEmpty(rt))
            return Results.Json(new { error = "no_refresh_token" }, statusCode: 401);

        var tokenUrl = config["Keycloak:TokenUrl"]!;
        var clientId = config["Keycloak:FrontendClientId"]!;

        var http = httpFactory.CreateClient();
        var kcResp = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["client_id"]     = clientId,
            ["refresh_token"] = rt,
        }));

        if (!kcResp.IsSuccessStatusCode)
        {
            ClearCookies(ctx.Response);
            return Results.Json(new { error = "session_expired" }, statusCode: 401);
        }

        var kc = await JsonSerializer.DeserializeAsync<KcTokenResponse>(
            await kcResp.Content.ReadAsStreamAsync(), JsonOpts);
        if (kc?.AccessToken is null) return Results.StatusCode(502);

        SetCookies(ctx.Response, kc);
        var user = DecodeJwtPayload(kc.AccessToken);
        return Results.Ok(BuildResponse(user, kc.ExpiresIn));
    }

    // ─── LOGOUT ──────────────────────────────────────────────────────────────
    private static IResult LogoutAsync(HttpContext ctx)
    {
        ClearCookies(ctx.Response);
        return Results.Ok();
    }

    // ─── HELPERS ─────────────────────────────────────────────────────────────
    private static CookieOptions MakeCookieOpts(TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        Secure   = false,   // prod'da true; HTTPS gerektirir
        SameSite = SameSiteMode.Strict,
        Path     = "/api",
        MaxAge   = maxAge,
    };

    private static void SetCookies(HttpResponse response, KcTokenResponse kc)
    {
        var atAge = TimeSpan.FromSeconds(kc.ExpiresIn        > 0 ? kc.ExpiresIn        : 300);
        var rtAge = TimeSpan.FromSeconds(kc.RefreshExpiresIn > 0 ? kc.RefreshExpiresIn : 1800);
        response.Cookies.Append("at", kc.AccessToken!,         MakeCookieOpts(atAge));
        response.Cookies.Append("rt", kc.RefreshToken ?? "",   MakeCookieOpts(rtAge));
    }

    private static void ClearCookies(HttpResponse response)
    {
        var gone = new CookieOptions { Path = "/api", MaxAge = TimeSpan.Zero };
        response.Cookies.Delete("at", gone);
        response.Cookies.Delete("rt", gone);
    }

    private static JwtPayload DecodeJwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return new JwtPayload();
        var pad  = parts[1] + new string('=', (4 - parts[1].Length % 4) % 4);
        var json = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(pad.Replace('-', '+').Replace('_', '/')));
        var doc  = JsonDocument.Parse(json).RootElement;

        var username = doc.TryGetProperty("preferred_username", out var u) ? u.GetString() ?? "" : "";
        var roles    = new List<string>();
        if (doc.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array)
            foreach (var role in r.EnumerateArray())
                if (role.GetString() is { } s) roles.Add(s);

        return new JwtPayload
        {
            Sub               = doc.TryGetProperty("sub",          out var sv) ? sv.GetString() ?? "" : "",
            PreferredUsername = username,
            PersonnelId       = doc.TryGetProperty("personnel_id", out var pv) ? pv.GetString()
                                : string.IsNullOrEmpty(username)               ? null
                                : username.ToUpperInvariant(),
            Roles             = roles,
            Exp               = doc.TryGetProperty("exp",          out var ev) ? ev.GetInt64() : 0,
        };
    }

    private static object BuildResponse(JwtPayload p, int expiresIn) => new
    {
        user = new
        {
            sub                = p.Sub,
            preferred_username = p.PreferredUsername,
            personnel_id       = p.PersonnelId,
            roles              = p.Roles,
        },
        expiresAt = p.Exp,
    };

    // ─── RECORDS / DTOs ───────────────────────────────────────────────────────
    private record LoginRequest(string Username, string Password);

    private class KcTokenResponse
    {
        [JsonPropertyName("access_token")]       public string? AccessToken      { get; set; }
        [JsonPropertyName("refresh_token")]      public string? RefreshToken     { get; set; }
        [JsonPropertyName("expires_in")]         public int     ExpiresIn        { get; set; }
        [JsonPropertyName("refresh_expires_in")] public int     RefreshExpiresIn { get; set; }
    }

    private class JwtPayload
    {
        public string       Sub               { get; set; } = "";
        public string       PreferredUsername { get; set; } = "";
        public string?      PersonnelId       { get; set; }
        public List<string> Roles             { get; set; } = [];
        public long         Exp               { get; set; }
    }
}
