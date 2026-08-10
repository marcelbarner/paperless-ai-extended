using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;

namespace PaperlessAI.API.Services;

public class DataSeeder(AppDbContext db, AppSettingsService settings, ILogger<DataSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await db.AiConnections.AnyAsync(ct))
        {
            logger.LogDebug("DataSeeder: Verbindungen bereits vorhanden – überspringe");
            return;
        }

        await SeedOpenAiAsync(ct);
        await SeedDocumentIntelligenceAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedOpenAiAsync(CancellationToken ct)
    {
        var endpoint = settings.Get("Azure:OpenAI:Endpoint");
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        var key = settings.Get("Azure:OpenAI:Key") ?? string.Empty;
        var deployment = settings.Get("Azure:OpenAI:DeploymentName") ?? "gpt-4o";

        var connection = new AiConnection
        {
            ServiceType = AiServiceType.OpenAI,
            Name = "Default Azure AI Service",
            Endpoint = endpoint,
            ApiKey = key
        };
        db.AiConnections.Add(connection);
        await db.SaveChangesAsync(ct);

        db.AiProviderConfigs.Add(new AiProviderConfig
        {
            ServiceType = AiServiceType.OpenAI,
            ConnectionId = connection.Id,
            DeploymentName = deployment,
            Priority = 1,
            IsEnabled = true
        });

        logger.LogInformation(
            "DataSeeder: 'Default Azure AI Service' aus bestehender Konfiguration angelegt (Deployment={D})",
            deployment);
    }

    private async Task SeedDocumentIntelligenceAsync(CancellationToken ct)
    {
        var endpoint = settings.Get("Azure:DocumentIntelligence:Endpoint");
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        var key = settings.Get("Azure:DocumentIntelligence:Key") ?? string.Empty;
        var outputFormat = settings.Get(DocumentIntelligenceService.OutputFormatKey) ?? "text";
        var model = settings.Get(DocumentIntelligenceService.ModelKey) ?? "auto";

        var connection = new AiConnection
        {
            ServiceType = AiServiceType.DocumentIntelligence,
            Name = "Default Azure Document Intelligence",
            Endpoint = endpoint,
            ApiKey = key
        };
        db.AiConnections.Add(connection);
        await db.SaveChangesAsync(ct);

        db.AiProviderConfigs.Add(new AiProviderConfig
        {
            ServiceType = AiServiceType.DocumentIntelligence,
            ConnectionId = connection.Id,
            OutputFormat = outputFormat,
            AnalysisModel = model,
            Priority = 1,
            IsEnabled = true
        });

        logger.LogInformation(
            "DataSeeder: 'Default Azure Document Intelligence' aus bestehender Konfiguration angelegt (Format={F}, Model={M})",
            outputFormat, model);
    }
}
