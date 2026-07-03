using Npgsql;
using System.Security.Claims;

namespace YonetimApi.Services;

public interface IPermissionService
{
    Task<bool> CanReadAsync(ClaimsPrincipal user, string personnelId);
    Task<bool> CanWriteAsync(ClaimsPrincipal user, string personnelId);
}

public class PersonnelPermissionService : IPermissionService
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<PersonnelPermissionService> _logger;

    public PersonnelPermissionService(NpgsqlDataSource db, ILogger<PersonnelPermissionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // permission=personnel.files  action=read   scope=self|team|all
    public Task<bool> CanReadAsync(ClaimsPrincipal user, string personnelId)
        => HasPermissionAsync(user, "personnel.files", "read", personnelId);

    // permission=personnel.files  action=write  scope=self|all
    public Task<bool> CanWriteAsync(ClaimsPrincipal user, string personnelId)
        => HasPermissionAsync(user, "personnel.files", "write", personnelId);

    // Faz C1 Aşama 2 — shadow mode: KARAR hâlâ JWT'den veriliyor (davranış değişmedi).
    // DB-tabanlı sonuç PARALEL hesaplanıp karşılaştırılıyor; uyuşmazlık varsa loglanır,
    // davranışı ETKİLEMEZ. Amaç: cutover'dan (Aşama 3) önce DB modelinin JWT ile birebir
    // aynı sonucu verdiğini gerçek trafikte kanıtlamak.
    private async Task<bool> HasPermissionAsync(
        ClaimsPrincipal user, string permission, string action, string targetId)
    {
        var jwtResult = await HasPermissionViaJwtAsync(user, permission, action, targetId);

        try
        {
            var ownId = GetPersonnelId(user);
            if (!string.IsNullOrEmpty(ownId))
            {
                var dbResult = await HasPermissionViaDbAsync(ownId, permission, action, targetId);
                if (dbResult != jwtResult)
                {
                    _logger.LogWarning(
                        "ROLE_SHADOW_MISMATCH principal={PrincipalId} permission={Permission} action={Action} target={TargetId} jwt={JwtResult} db={DbResult}",
                        ownId, permission, action, targetId, jwtResult, dbResult);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ROLE_SHADOW_ERROR permission={Permission} action={Action} target={TargetId}",
                permission, action, targetId);
        }

        return jwtResult;
    }

    // Kural: all → team → self sırasıyla kontrol edilir.
    // Role formatı: {permission}.{action}.{scope}
    // Örnek: personnel.files.read.team
    private async Task<bool> HasPermissionViaJwtAsync(
        ClaimsPrincipal user, string permission, string action, string targetId)
    {
        if (HasRole(user, $"{permission}.{action}.all"))
            return true;

        var ownId = GetPersonnelId(user);
        if (string.IsNullOrEmpty(ownId))
            return false;

        if (HasRole(user, $"{permission}.{action}.team"))
            return ownId == targetId || await IsTeamMemberAsync(ownId, targetId);

        if (HasRole(user, $"{permission}.{action}.self"))
            return ownId == targetId;

        return false;
    }

    // yonetim.role_assignments'tan AYNI all → team → self önceliğiyle kontrol eder.
    private async Task<bool> HasPermissionViaDbAsync(
        string ownId, string permission, string action, string targetId)
    {
        if (await HasDbRoleAsync(ownId, permission, action, "all"))
            return true;

        if (await HasDbRoleAsync(ownId, permission, action, "team"))
            return ownId == targetId || await IsTeamMemberAsync(ownId, targetId);

        if (await HasDbRoleAsync(ownId, permission, action, "self"))
            return ownId == targetId;

        return false;
    }

    private async Task<bool> HasDbRoleAsync(string principalId, string permission, string action, string scope)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT 1 FROM yonetim.role_assignments " +
            "WHERE principal_id = $1 AND permission = $2 AND action = $3 AND scope = $4 AND revoked_at IS NULL");
        cmd.Parameters.AddWithValue(principalId);
        cmd.Parameters.AddWithValue(permission);
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.AddWithValue(scope);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static bool HasRole(ClaimsPrincipal user, string role)
        => user.FindAll("roles").Any(c => c.Value == role);

    private static string? GetPersonnelId(ClaimsPrincipal user)
    {
        var claimValue = user.FindFirst("personnel_id")?.Value;
        if (!string.IsNullOrWhiteSpace(claimValue))
            return claimValue;

        var username = user.FindFirst("preferred_username")?.Value
                       ?? user.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(username)
            ? null
            : username.ToUpperInvariant();
    }

    private async Task<bool> IsTeamMemberAsync(string managerId, string personnelId)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT 1 FROM yonetim.team_members WHERE manager_id = $1 AND personnel_id = $2");
        cmd.Parameters.AddWithValue(managerId);
        cmd.Parameters.AddWithValue(personnelId);
        return await cmd.ExecuteScalarAsync() is not null;
    }
}
