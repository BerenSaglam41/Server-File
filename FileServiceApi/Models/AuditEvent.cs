namespace FileServiceApi.Models;

public class AuditEvent
{
    public long Id { get; set; }
    public Guid? FileId { get; set; }
    public string AppCode { get; set; } = string.Empty;
    public string? Actor { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public string? CorrelationId { get; set; }
    public string? ActorIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
}