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

    public PersonnelPermissionService(NpgsqlDataSource db) => _db = db;

    // permission=personnel.files  action=read   scope=self|team|all
    public Task<bool> CanReadAsync(ClaimsPrincipal user, string personnelId)
        => HasPermissionAsync(user, "personnel.files", "read", personnelId);

    // permission=personnel.files  action=write  scope=self|all
    public Task<bool> CanWriteAsync(ClaimsPrincipal user, string personnelId)
        => HasPermissionAsync(user, "personnel.files", "write", personnelId);

    // Kural: all → team → self sırasıyla kontrol edilir.
    // Role formatı: {permission}.{action}.{scope}
    // Örnek: personnel.files.read.team
    private async Task<bool> HasPermissionAsync(
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
