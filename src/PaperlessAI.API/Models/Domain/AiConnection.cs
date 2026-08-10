namespace PaperlessAI.API.Models.Domain;

public class AiConnection
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;

    public ICollection<AiProviderConfig> ProviderConfigs { get; set; } = [];
}
