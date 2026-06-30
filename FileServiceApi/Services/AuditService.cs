using FileServiceApi.Data;
using FileServiceApi.Models;

namespace FileServiceApi.Services;

public class AuditService
{
    private readonly AppDbContext _db;

    public AuditService(AppDbContext db)
    {
        _db = db;
    }

    public async Task WriteAsync(Guid? fileId, string? appCode, string? actor, string action, string result, string? reasonCode, string? correlationId, string? actorIp = null, string? userAgent = null)
    {
        _db.AuditEvents.Add(new AuditEvent
        {
            FileId = fileId,
            AppCode = appCode ?? "unknown",
            Actor = actor,
            Action = action,
            Result = result,
            ReasonCode = reasonCode,
            CorrelationId = correlationId,
            ActorIp = actorIp,
            UserAgent = userAgent,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }
}