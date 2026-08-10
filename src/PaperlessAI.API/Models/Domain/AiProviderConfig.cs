namespace PaperlessAI.API.Models.Domain;

public enum AiServiceType { OpenAI, DocumentIntelligence }

public class AiProviderConfig
{
    public int Id { get; set; }
    public AiServiceType ServiceType { get; set; }
    public int ConnectionId { get; set; }
    public AiConnection Connection { get; set; } = null!;
    public string? ModelOverride { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
}
