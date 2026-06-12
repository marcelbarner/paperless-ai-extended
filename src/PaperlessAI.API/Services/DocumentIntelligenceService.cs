using Azure;
using Azure.AI.DocumentIntelligence;

namespace PaperlessAI.API.Services;

public class DocumentIntelligenceService(AppSettingsService settings, ILogger<DocumentIntelligenceService> logger)
{
    public const string OutputFormatKey = "Azure:DocumentIntelligence:OutputFormat";
    public const string ModelKey = "Azure:DocumentIntelligence:Model";

    private DocumentIntelligenceClient BuildClient()
    {
        var endpoint = settings.Get("Azure:DocumentIntelligence:Endpoint")
            ?? throw new InvalidOperationException("Azure:DocumentIntelligence:Endpoint not configured.");
        var key = settings.Get("Azure:DocumentIntelligence:Key")
            ?? throw new InvalidOperationException("Azure:DocumentIntelligence:Key not configured.");

        return new DocumentIntelligenceClient(new Uri(endpoint), new AzureKeyCredential(key));
    }

    public async Task<string> AnalyzeDocumentAsync(byte[] pdfBytes, CancellationToken ct = default)
    {
        var formatSetting = settings.Get(OutputFormatKey) ?? "text";
        var isMarkdown = formatSetting.Equals("markdown", StringComparison.OrdinalIgnoreCase);

        var outputFormat = isMarkdown
            ? DocumentContentFormat.Markdown
            : DocumentContentFormat.Text;

        // prebuilt-layout wird für Markdown benötigt (erkennt Tabellen/Überschriften)
        // prebuilt-read liefert nur Fließtext und ignoriert das Format-Flag weitgehend
        var modelOverride = settings.Get(ModelKey);
        var modelId = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride
            : isMarkdown ? "prebuilt-layout" : "prebuilt-read";

        logger.LogInformation(
            "Azure Document Intelligence: {Bytes} Bytes | Modell={Model} | Format={Format}",
            pdfBytes.Length, modelId, formatSetting);

        var client = BuildClient();
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
