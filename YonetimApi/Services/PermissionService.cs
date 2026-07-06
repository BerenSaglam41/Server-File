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
    private readonly string _roleSource;

    public PersonnelPermissionService(NpgsqlDataSource db, ILogger<PersonnelPermissionService> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;
        // Faz C1 Aşama 3 — cutover flag'i. "Jwt": sadece JWT (Aşama 1 öncesi davranış, rollback).
        // "Shadow": ikisi de hesaplanır, JWT karar verir (Aşama 2, varsayılan). "Db": ikisi de
        // hesaplanır (gözlem kaybolmasın diye), ama artık DB karar verir (Aşama 3 cutover).
        _roleSource = config["Authorization:RoleSource"] ?? "Shadow";
    }

    // permission=personnel.files  action=read   scope=self|team|all
    public Task<bool> CanReadAsync(ClaimsPrincipal user, string personnelId)
        => HasPermissionAsync(user, "personnel.files", "read", personnelId);

    // permission=personnel.files  action=write  scope=self|all
    public Task<bool> CanWriteAsync(ClaimsPrincipal user, string personnelId)
        => HasPermissionAsync(user, "personnel.files", "write", personnelId);

    private async Task<bool> HasPermissionAsync(
        ClaimsPrincipal user, string permission, string action, string targetId)
    {
        if (_roleSource == "Jwt")
            return await HasPermissionViaJwtAsync(user, permission, action, targetId);

        var jwtResult = await HasPermissionViaJwtAsync(user, permission, action, targetId);
        var ownId = GetPersonnelId(user);
        bool dbResult = jwtResult; // DB sorgusu başarısız olursa (ownId yok/hata) JWT'ye düş

        try
        {
            if (!string.IsNullOrEmpty(ownId))
            {
                dbResult = await HasPermissionViaDbAsync(ownId, permission, action, targetId);
                if (dbResult != jwtResult)
                {
                    _logger.LogWarning(
                        "ROLE_SHADOW_MISMATCH principal={PrincipalId} permission={Permission} action={Action} target={TargetId} jwt={JwtResult} db={DbResult} roleSource={RoleSource}",
                        ownId, permission, action, targetId, jwtResult, dbResult, _roleSource);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ROLE_SHADOW_ERROR permission={Permission} action={Action} target={TargetId}",
                permission, action, targetId);
            dbResult = jwtResult; // hata durumunda JWT'ye düş (fail-open değil, fail-to-previous-known-good)
        }

        return _roleSource == "Db" ? dbResult : jwtResult;
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
