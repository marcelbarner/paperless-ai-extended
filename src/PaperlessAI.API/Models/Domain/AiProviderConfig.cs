namespace PaperlessAI.API.Models.Domain;

public enum AiServiceType { OpenAI, DocumentIntelligence }

public class AiProviderConfig
{
    public int Id { get; set; }
    public AiServiceType ServiceType { get; set; }
    public int ConnectionId { get; set; }
    public AiConnection Connection { get; set; } = null!;
    public string? DeploymentName { get; set; }   // OpenAI: Deployment
    public string? OutputFormat { get; set; }      // DocumentIntelligence: text | markdown
    public string? AnalysisModel { get; set; }     // DocumentIntelligence: prebuilt-read | prebuilt-layout | auto
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
}
