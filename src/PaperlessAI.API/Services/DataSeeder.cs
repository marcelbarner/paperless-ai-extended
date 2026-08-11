using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;

namespace PaperlessAI.API.Services;

public class DataSeeder(AppDbContext db, AppSettingsService settings, ILogger<DataSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedOpenAiIfMissingAsync(ct);
        await SeedDocumentIntelligenceIfMissingAsync(ct);
    }

    private async Task SeedOpenAiIfMissingAsync(CancellationToken ct)
    {
        if (await db.AiProviderConfigs.AnyAsync(p => p.ServiceType == AiServiceType.OpenAI, ct))
        {
            logger.LogDebug("DataSeeder: OpenAI Provider-Konfiguration bereits vorhanden – überspringe");
            return;
        }

        var endpoint = settings.Get("Azure:OpenAI:Endpoint");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            logger.LogDebug("DataSeeder: Azure:OpenAI:Endpoint nicht konfiguriert – überspringe OpenAI-Seed");
            return;
        }

        var connection = await db.AiConnections
            .FirstOrDefaultAsync(c => c.ServiceType == AiServiceType.OpenAI, ct);

        if (connection is null)
        {
            connection = new AiConnection
            {
                ServiceType = AiServiceType.OpenAI,
                Name = "Default Azure AI Service",
                Endpoint = endpoint,
                ApiKey = settings.Get("Azure:OpenAI:Key") ?? string.Empty
            };
            db.AiConnections.Add(connection);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("DataSeeder: Verbindung 'Default Azure AI Service' angelegt");
        }

        var deployment = settings.Get("Azure:OpenAI:DeploymentName") ?? "gpt-4o";
        db.AiProviderConfigs.Add(new AiProviderConfig
        {
            ServiceType = AiServiceType.OpenAI,
            ConnectionId = connection.Id,
            DeploymentName = deployment,
            Priority = 1,
            IsEnabled = true
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "DataSeeder: OpenAI Provider-Konfiguration angelegt (Deployment={D})", deployment);
    }

    private async Task SeedDocumentIntelligenceIfMissingAsync(CancellationToken ct)
    {
        if (await db.AiProviderConfigs.AnyAsync(p => p.ServiceType == AiServiceType.DocumentIntelligence, ct))
        {
            logger.LogDebug("DataSeeder: Document Intelligence Provider-Konfiguration bereits vorhanden – überspringe");
            return;
        }

        var endpoint = settings.Get("Azure:DocumentIntelligence:Endpoint");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            logger.LogDebug("DataSeeder: Azure:DocumentIntelligence:Endpoint nicht konfiguriert – überspringe DocIntel-Seed");
            return;
        }

        var connection = await db.AiConnections
            .FirstOrDefaultAsync(c => c.ServiceType == AiServiceType.DocumentIntelligence, ct);

        if (connection is null)
        {
            connection = new AiConnection
            {
                ServiceType = AiServiceType.DocumentIntelligence,
                Name = "Default Azure Document Intelligence",
                Endpoint = endpoint,
                ApiKey = settings.Get("Azure:DocumentIntelligence:Key") ?? string.Empty
            };
            db.AiConnections.Add(connection);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("DataSeeder: Verbindung 'Default Azure Document Intelligence' angelegt");
        }

        var outputFormat = settings.Get(DocumentIntelligenceService.OutputFormatKey) ?? "text";
        var model = settings.Get(DocumentIntelligenceService.ModelKey) ?? "auto";
        db.AiProviderConfigs.Add(new AiProviderConfig
        {
            ServiceType = AiServiceType.DocumentIntelligence,
            ConnectionId = connection.Id,
            OutputFormat = outputFormat,
            AnalysisModel = model,
            Priority = 1,
            IsEnabled = true
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "DataSeeder: Document Intelligence Provider-Konfiguration angelegt (Format={F}, Model={M})",
            outputFormat, model);
    }
}
