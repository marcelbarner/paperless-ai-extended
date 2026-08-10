using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;
using PaperlessAI.API.Queue;
using PaperlessAI.API.Services;

namespace PaperlessAI.API.BackgroundServices;

public class PaperlessPollingService(
    IServiceScopeFactory scopeFactory,
    DocumentProcessingChannel channel,
    AppSettingsService settings,
    ILogger<PaperlessPollingService> logger) : BackgroundService
{
    public const string OcrTagKey = "Paperless:OcrTagName";
    public const string AiTagKey = "Paperless:AiTagName";
    public const string ErrorTagKey = "Paperless:ErrorTagName";
    public const string ReviewTagKey = "Paperless:ReviewTagName";
    public const string PollingEnabledKey = "Polling:Enabled";
    public const string OcrEnabledKey = "Processing:OcrEnabled";
    public const string AiEnabledKey = "Processing:AiEnabled";
    public const string DefaultOcrTagName = "paperless-ai-ocr";
    public const string DefaultAiTagName = "paperless-ai-process";
    public const string DefaultErrorTagName = "paperless-ai-error";
    public const string DefaultReviewTagName = "paperless-ai-review";

    private string ErrorTagName => settings.Get(ErrorTagKey) ?? DefaultErrorTagName;
    private string ReviewTagName => settings.Get(ReviewTagKey) ?? DefaultReviewTagName;
    private string OcrTagName => settings.Get(OcrTagKey) ?? DefaultOcrTagName;
    private string AiTagName => settings.Get(AiTagKey) ?? DefaultAiTagName;
    private bool OcrEnabled => settings.Get(OcrEnabledKey) != "false";
    private bool AiEnabled => settings.Get(AiEnabledKey) != "false";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureTagsExistAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = int.TryParse(settings.Get("Polling:IntervalSeconds"), out var i) ? i : 30;
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Fehler beim Polling");
            }
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (settings.Get(PollingEnabledKey) == "false")
        {
            logger.LogDebug("Polling deaktiviert – überspringe");
            return;
        }

        var ocrEnabled = OcrEnabled;
        var aiEnabled = AiEnabled;

        if (!ocrEnabled && !aiEnabled)
        {
            logger.LogDebug("OCR und KI-Verarbeitung deaktiviert – überspringe Poll");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var paperless = scope.ServiceProvider.GetRequiredService<PaperlessClient>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ocrTagName = OcrTagName;
        var aiTagName = AiTagName;

        var ocrTag = ocrEnabled ? await paperless.GetTagByNameAsync(ocrTagName, ct) : null;
        var aiTag = aiEnabled ? await paperless.GetTagByNameAsync(aiTagName, ct) : null;

        if (ocrTag is null && aiTag is null)
        {
            logger.LogDebug("Keine aktiven Verarbeitungs-Tags gefunden, überspringe Poll");
            return;
        }

        var tagNamesToSearch = new List<string>();
        if (ocrTag is not null) tagNamesToSearch.Add(ocrTagName);
        if (aiTag is not null) tagNamesToSearch.Add(aiTagName);

        var documents = await paperless.GetDocumentsWithTagsAsync(tagNamesToSearch, ct);
        logger.LogDebug("Poll: {Count} Dokument(e) mit Verarbeitungs-Tags gefunden", documents.Count);

        var errorTag = await paperless.GetTagByNameAsync(ErrorTagName, ct);

        foreach (var doc in documents)
        {
            var hasOcr = ocrTag is not null && doc.Tags.Contains(ocrTag.Id);
            var hasAi = aiTag is not null && doc.Tags.Contains(aiTag.Id);

            if (errorTag is not null && doc.Tags.Contains(errorTag.Id))
            {
                logger.LogDebug("Dokument {DocId} hat Fehler-Tag – wird übersprungen", doc.Id);
                continue;
            }

            if (ocrEnabled && hasOcr)
            {
                var ocrAlreadyQueued = await db.ProcessingJobs.AnyAsync(
                    j => j.DocumentId == doc.Id && j.JobType == JobType.Ocr &&
                         (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing), ct);

                if (!ocrAlreadyQueued)
                {
                    var job = await CreateJobAsync(db, doc.Id, doc.Title, JobType.Ocr, ct);
                    await channel.Writer.WriteAsync(job, ct);
                    logger.LogInformation(
                        "OCR-Job #{Id} für Dokument '{Title}' (DocId={DocId}) eingereiht",
                        job.Id, doc.Title, doc.Id);
                }
            }

            // AI-Job nur wenn: AI aktiviert, AI-Tag vorhanden, OCR-Tag NICHT vorhanden
            if (aiEnabled && hasAi && !hasOcr)
            {
                var aiAlreadyQueued = await db.ProcessingJobs.AnyAsync(
                    j => j.DocumentId == doc.Id && j.JobType == JobType.Ai &&
                         (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing), ct);

                if (!aiAlreadyQueued)
                {
                    var job = await CreateJobAsync(db, doc.Id, doc.Title, JobType.Ai, ct);
                    await channel.Writer.WriteAsync(job, ct);
                    logger.LogInformation(
                        "AI-Job #{Id} für Dokument '{Title}' (DocId={DocId}) eingereiht",
                        job.Id, doc.Title, doc.Id);
                }
            }

            if (hasOcr && hasAi && !ocrEnabled)
            {
                logger.LogDebug(
                    "Dokument {DocId} hat OCR-Tag, aber OCR ist deaktiviert – AI-Job wird nicht erstellt",
                    doc.Id);
            }
        }
    }

    private static async Task<ProcessingJob> CreateJobAsync(
        AppDbContext db, int documentId, string title, JobType type, CancellationToken ct)
    {
        var job = new ProcessingJob
        {
            DocumentId = documentId,
            DocumentTitle = title,
            JobType = type,
            Status = JobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.ProcessingJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    private async Task EnsureTagsExistAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var paperless = scope.ServiceProvider.GetRequiredService<PaperlessClient>();

        await EnsureTagAsync(paperless, OcrTagName, "#1565C0", ct);
        await EnsureTagAsync(paperless, AiTagName, "#2E7D32", ct);
        await EnsureTagAsync(paperless, ErrorTagName, "#C62828", ct);
        await EnsureTagAsync(paperless, ReviewTagName, "#F59E0B", ct);

        logger.LogInformation(
            "Polling gestartet – OCR-Tag: '{Ocr}', AI-Tag: '{Ai}', Fehler-Tag: '{Err}', Review-Tag: '{Rev}'",
            OcrTagName, AiTagName, ErrorTagName, ReviewTagName);
    }

    private async Task EnsureTagAsync(PaperlessClient client, string name, string color, CancellationToken ct)
    {
        try
        {
            var existing = await client.GetTagByNameAsync(name, ct);
            if (existing is null)
            {
                var created = await client.CreateTagAsync(name, color, ct);
                logger.LogInformation("Paperless-Tag '{Name}' angelegt (id={Id})", name, created.Id);
            }
            else
            {
                logger.LogDebug("Paperless-Tag '{Name}' vorhanden (id={Id})", name, existing.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Konnte Tag '{Name}' nicht prüfen/anlegen (Paperless nicht erreichbar?)", name);
        }
    }
}
