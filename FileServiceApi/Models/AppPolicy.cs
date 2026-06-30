namespace FileServiceApi.Models;

public class AppPolicy
{
    public string AppCode { get; set; } = string.Empty;
    public List<string> AllowedDomains { get; set; } = new();
    public List<string> AllowedFileTypes { get; set; } = new();
    public bool CanCreate { get; set; }
    public bool CanRead { get; set; }
    public bool CanArchive { get; set; }
    public long MaxFileSizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}