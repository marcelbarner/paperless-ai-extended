namespace PaperlessAI.API.Models.Domain;

public class MetadataDescription
{
    public int Id { get; set; }
    public EntityType EntityType { get; set; }
    public int EntityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public enum EntityType
{
    Correspondent,
    DocumentType,
    Tag,
    StoragePath,
    CustomField
}
