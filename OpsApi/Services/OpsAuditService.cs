using Npgsql;

namespace OpsApi.Services;

public sealed class OpsAuditService(NpgsqlDataSource db)
{
    private static readonly Dictionary<string, string> ActionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/ops/health"]   = "ops.health.read",
        ["/ops/services"] = "ops.services.list",
        ["/ops/disk"]     = "ops.disk.read",
        ["/ops/alerts"]   = "ops.alerts.list",
        ["/ops/backups"]  = "ops.backups.list",
        ["/ops/version"]  = "ops.version.read",
    };

    public static string MapAction(string method, string path)
    {
        if (ActionMap.TryGetValue(path, out var action)) return action;
        return $"ops.{path.TrimStart('/').Replace('/', '.')}.{method.ToLower()}";
    }

    public async Task WriteAsync(
        string  actor,
        string  action,
        string  result,
        string? reasonCode    = null,
        string? correlationId = null,
        string? ip            = null,
        string? path          = null,
        string? method        = null,
        int?    durationMs    = null)
    {
        try
        {
            await using var conn = await db.OpenConnectionAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ops.audit_events
                    (actor, action, result, reason_code, correlation_id, ip, path, method, duration_ms)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)";

            cmd.Parameters.AddWithValue(actor);
            cmd.Parameters.AddWithValue(action);
            cmd.Parameters.AddWithValue(result);
            cmd.Parameters.AddWithValue((object?)reasonCode    ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)correlationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ip            ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)path          ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)method        ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)durationMs    ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Audit yazma hatası ana isteği kesmemeli
        }
    }
}
