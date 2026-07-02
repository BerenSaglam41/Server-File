namespace FileServiceApi.Models;

public class DownloadTicket
{
    public string TicketHash { get; set; } = string.Empty;
    public Guid FileId { get; set; }
    public string AppCode { get; set; } = string.Empty;
    public string? Actor { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public int UseCount { get; set; }
    public DateTime CreatedAt { get; set; }
}
