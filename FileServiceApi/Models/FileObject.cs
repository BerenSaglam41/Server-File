namespace FileServiceApi.Models;

public class FileObject
{
    public Guid FileId { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Classification { get; set; } = "internal";
    public string? RetentionPolicy { get; set; }
    public string Status { get; set; } = "active";
    public string CreatedByApp { get; set; } = string.Empty;
    public string? CreatedByUser { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}