namespace FileServiceApi.Models;

public class RelationTypeConfig
{
    public string RelationType { get; set; } = string.Empty;
    public string Cardinality  { get; set; } = "single";
    public string? Description  { get; set; }
}
