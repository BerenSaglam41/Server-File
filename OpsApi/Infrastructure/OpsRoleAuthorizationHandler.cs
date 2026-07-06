using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace OpsApi.Infrastructure;

public class OpsRoleRequirement : IAuthorizationRequirement
{
    public string[] AllowedRoles { get; }
    public OpsRoleRequirement(params string[] allowedRoles) => AllowedRoles = allowedRoles;
}

// Faz C1 — yerel yetkilendirme göçü. RoleSource=Jwt: sadece JWT (rollback). Shadow: ikisi
// de hesaplanır, JWT karar verir (Aşama 2). Db: ikisi de hesaplanır, DB karar verir
// (Aşama 3 cutover). Eski statik HasOpsRole fonksiyonunun yerine DI-uyumlu bir
// AuthorizationHandler'a taşındı çünkü RequireAssertion senkron çalışır, DB sorgusu için
// async bir handler'a ihtiyaç var.
public class OpsRoleAuthorizationHandler : AuthorizationHandler<OpsRoleRequirement>
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<OpsRoleAuthorizationHandler> _logger;
    private readonly string _roleSource;

    public OpsRoleAuthorizationHandler(NpgsqlDataSource db, ILogger<OpsRoleAuthorizationHandler> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;
        _roleSource = config["Authorization:RoleSource"] ?? "Shadow";
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OpsRoleRequirement requirement)
    {
        var jwtResult = HasOpsRoleViaJwt(context.User, requirement.AllowedRoles);

        if (_roleSource == "Jwt")
        {
            if (jwtResult) context.Succeed(requirement);
            return;
        }

        var principalId = GetPrincipalId(context.User);
        var dbResult = jwtResult; // DB sorgusu başarısız olursa (principalId yok/hata) JWT'ye düş

        try
        {
            if (!string.IsNullOrEmpty(principalId))
            {
                dbResult = await HasOpsRoleViaDbAsync(principalId, requirement.AllowedRoles);
                if (dbResult != jwtResult)
                {
                    _logger.LogWarning(
                        "ROLE_SHADOW_MISMATCH principal={PrincipalId} allowedRoles={AllowedRoles} jwt={JwtResult} db={DbResult} roleSource={RoleSource}",
                        principalId, string.Join(",", requirement.AllowedRoles), jwtResult, dbResult, _roleSource);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ROLE_SHADOW_ERROR allowedRoles={AllowedRoles}", string.Join(",", requirement.AllowedRoles));
            dbResult = jwtResult;
        }

        var effectiveResult = _roleSource == "Db" ? dbResult : jwtResult;
        if (effectiveResult)
            context.Succeed(requirement);
    }

    private static bool HasOpsRoleViaJwt(ClaimsPrincipal user, string[] allowed)
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

    private static string? GetPrincipalId(ClaimsPrincipal user)
    {
        var claimValue = user.FindFirst("personnel_id")?.Value;
        if (!string.IsNullOrWhiteSpace(claimValue))
            return claimValue;

        var username = user.FindFirst("preferred_username")?.Value ?? user.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(username) ? null : username.ToUpperInvariant();
    }

    // allowedRoles: "ops.read"/"ops.execute"/"ops.admin" — permission='ops', action=son parça.
    private async Task<bool> HasOpsRoleViaDbAsync(string principalId, string[] allowedRoles)
    {
        foreach (var role in allowedRoles)
        {
            var action = role.Split('.').Last();
            await using var cmd = _db.CreateCommand(
                "SELECT 1 FROM yonetim.role_assignments " +
                "WHERE principal_id = $1 AND permission = 'ops' AND action = $2 AND revoked_at IS NULL");
            cmd.Parameters.AddWithValue(principalId);
            cmd.Parameters.AddWithValue(action);
            if (await cmd.ExecuteScalarAsync() is not null)
                return true;
        }
        return false;
    }
}
