using System.Text.Json.Serialization;

namespace PaperlessAI.API.Models.Paperless;

public class PaperlessTag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Color { get; set; } = "#000000";

    [JsonPropertyName("is_inbox_tag")]
    public bool IsInboxTag { get; set; }
}

public class PaperlessCorrespondent
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
}

public class PaperlessDocumentType
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
}

public class PaperlessStoragePath
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public class PaperlessCustomField
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("data_type")]
    public string DataType { get; set; } = string.Empty;
}
