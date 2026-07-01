namespace FileServiceApi.Models;

public class FileReference
{
    public long Id { get; set; }
    public Guid FileId { get; set; }
    public string AppCode { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string RelationType { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; }
}
