using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;

namespace PaperlessAI.API.Services;

public class DocumentIntelligenceService(
    AppSettingsService settings,
    AppDbContext db,
    ILogger<DocumentIntelligenceService> logger)
{
    public const string OutputFormatKey = "Azure:DocumentIntelligence:OutputFormat";
    public const string ModelKey = "Azure:DocumentIntelligence:Model";

    private static readonly int[] FallbackStatusCodes = [401, 429, 503];

    public async Task<string> AnalyzeDocumentAsync(byte[] pdfBytes, CancellationToken ct = default)
    {
        var formatSetting = settings.Get(OutputFormatKey) ?? "text";
        var isMarkdown = formatSetting.Equals("markdown", StringComparison.OrdinalIgnoreCase);
        var outputFormat = isMarkdown ? DocumentContentFormat.Markdown : DocumentContentFormat.Text;
        var modelOverrideSetting = settings.Get(ModelKey);
        var defaultModel = !string.IsNullOrWhiteSpace(modelOverrideSetting)
            ? modelOverrideSetting
            : isMarkdown ? "prebuilt-layout" : "prebuilt-read";

        var providers = await db.AiProviderConfigs
            .Include(p => p.Connection)
            .Where(p => p.ServiceType == AiServiceType.DocumentIntelligence && p.IsEnabled)
            .OrderBy(p => p.Priority)
            .ToListAsync(ct);

        if (providers.Count == 0)
        {
            var endpoint = settings.Get("Azure:DocumentIntelligence:Endpoint")
                ?? throw new InvalidOperationException("Azure:DocumentIntelligence:Endpoint not configured.");
            var key = settings.Get("Azure:DocumentIntelligence:Key")
                ?? throw new InvalidOperationException("Azure:DocumentIntelligence:Key not configured.");
            return await AnalyzeWithClientAsync(endpoint, key, defaultModel, outputFormat, formatSetting, pdfBytes, ct);
        }

        Exception? lastException = null;
        foreach (var provider in providers)
        {
            try
            {
                var model = provider.ModelOverride ?? defaultModel;
                logger.LogInformation(
                    "Document Intelligence: Versuche Provider '{Name}' (Prio={Prio}, Modell={Model})",
                    provider.Connection.Name, provider.Priority, model);
                return await AnalyzeWithClientAsync(
                    provider.Connection.Endpoint, provider.Connection.ApiKey,
                    model, outputFormat, formatSetting, pdfBytes, ct);
            }
            catch (RequestFailedException ex) when (FallbackStatusCodes.Contains(ex.Status))
            {
                logger.LogWarning(
                    "Document Intelligence Provider '{Name}' fehlgeschlagen mit HTTP {Status} – nächster Provider wird versucht",
                    provider.Connection.Name, ex.Status);
                lastException = ex;
            }
        }

        throw lastException ?? new InvalidOperationException("Alle Document Intelligence Provider fehlgeschlagen");
    }

    private async Task<string> AnalyzeWithClientAsync(
        string endpoint, string key, string modelId,
        DocumentContentFormat outputFormat, string formatSetting,
        byte[] pdfBytes, CancellationToken ct)
    {
        logger.LogInformation(
            "Azure Document Intelligence: {Bytes} Bytes | Modell={Model} | Format={Format}",
            pdfBytes.Length, modelId, formatSetting);

        var client = new DocumentIntelligenceClient(new Uri(endpoint), new AzureKeyCredential(key));
        var options = new AnalyzeDocumentOptions(modelId, BinaryData.FromBytes(pdfBytes))
        {
            OutputContentFormat = outputFormat
        };

        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, options, ct);
        var content = operation.Value.Content;

        logger.LogInformation(
            "Azure Document Intelligence: {Chars} Zeichen zurückgegeben (Format={Format})",
            content.Length, formatSetting);

        return content;
    }
}
