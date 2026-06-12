using System.Text.Json.Serialization;

namespace PaperlessAI.API.Models.Paperless;

public class PaperlessDocument
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("correspondent")]
    public int? CorrespondentId { get; set; }

    [JsonPropertyName("document_type")]
    public int? DocumentTypeId { get; set; }

    [JsonPropertyName("storage_path")]
    public int? StoragePathId { get; set; }

    public List<int> Tags { get; set; } = [];

    [JsonPropertyName("custom_fields")]
    public List<PaperlessCustomFieldValue> CustomFields { get; set; } = [];

    [JsonPropertyName("original_file_name")]
    public string OriginalFileName { get; set; } = string.Empty;

    [JsonPropertyName("created_date")]
    public string? CreatedDate { get; set; }
}

public class PaperlessCustomFieldValue
{
    public int Field { get; set; }
    public object? Value { get; set; }
}

public class PaperlessPagedResult<T>
{
    public int Count { get; set; }
    public string? Next { get; set; }
    public List<T> Results { get; set; } = [];
}
