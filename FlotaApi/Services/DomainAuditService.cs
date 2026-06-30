using Npgsql;

namespace FlotaApi.Services;

public interface IDomainAuditService
{
    Task WriteAsync(string vehicleId, string actor, string action, string result, string? reasonCode = null, string? correlationId = null);
}

public class DomainAuditService : IDomainAuditService
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<DomainAuditService> _logger;

    public DomainAuditService(NpgsqlDataSource db, ILogger<DomainAuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task WriteAsync(string vehicleId, string actor, string action, string result, string? reasonCode = null, string? correlationId = null)
    {
        try
        {
            await using var cmd = _db.CreateCommand(
                "INSERT INTO filo.audit_events (vehicle_id, actor, action, result, reason_code, correlation_id) " +
                "VALUES ($1, $2, $3, $4, $5, $6)");
            cmd.Parameters.AddWithValue(vehicleId);
            cmd.Parameters.AddWithValue(actor);
            cmd.Parameters.AddWithValue(action);
            cmd.Parameters.AddWithValue(result);
            cmd.Parameters.AddWithValue((object?)reasonCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)correlationId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Domain audit yazılamadı: vehicle={VehicleId} action={Action}", vehicleId, action);
        }
    }
}
