using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;
using PaperlessAI.API.Queue;
using PaperlessAI.API.Services;

namespace PaperlessAI.API.BackgroundServices;

public class DocumentProcessingWorker(
    IServiceScopeFactory scopeFactory,
    DocumentProcessingChannel channel,
    AppSettingsService settings,
    ILogger<DocumentProcessingWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverPendingJobsAsync(stoppingToken);

        await foreach (var job in channel.Reader.ReadAllAsync(stoppingToken))
        {
            await ProcessJobAsync(job, stoppingToken);
        }
    }

    private async Task RecoverPendingJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stuck = await db.ProcessingJobs
            .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Processing)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        if (stuck.Count == 0) return;

        logger.LogInformation("Startup-Recovery: {Count} unverarbeitete Job(s) werden neu eingereiht", stuck.Count);

        foreach (var job in stuck)
        {
            job.Status = JobStatus.Pending;
            job.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        foreach (var job in stuck)
            await channel.Writer.WriteAsync(job, ct);
    }

    private async Task ProcessJobAsync(ProcessingJob job, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var paperless = scope.ServiceProvider.GetRequiredService<PaperlessClient>();
        var docIntel = scope.ServiceProvider.GetRequiredService<DocumentIntelligenceService>();
        var openAi = scope.ServiceProvider.GetRequiredService<OpenAIService>();
        var contextBuilder = scope.ServiceProvider.GetRequiredService<MetadataContextBuilder>();

        await SetStatus(db, job.Id, JobStatus.Processing, ct);
        logger.LogInformation("Starte Job #{Id} | Typ: {Type} | Dokument: '{Title}' (DocId={DocId})",
            job.Id, job.JobType, job.DocumentTitle, job.DocumentId);

        try
        {
            switch (job.JobType)
            {
                case JobType.Ocr:
                    await RunOcrAsync(db, paperless, docIntel, job, ct);
                    break;

                case JobType.Ai:
                    await RunAiAsync(db, paperless, openAi, contextBuilder, job, ct);
                    break;

                case JobType.Both:
                    // Legacy: beide Schritte sequenziell
                    await RunOcrAsync(db, paperless, docIntel, job, ct, skipDone: true);
                    await RunAiAsync(db, paperless, openAi, contextBuilder, job, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job #{Id} fehlgeschlagen", job.Id);
            await SetStatus(db, job.Id, JobStatus.Failed, ct, error: BuildErrorMessage(ex));
        }
    }

    // ── OCR ──────────────────────────────────────────────────────────────────

    private async Task RunOcrAsync(
        AppDbContext db, PaperlessClient paperless, DocumentIntelligenceService docIntel,
        ProcessingJob job, CancellationToken ct, bool skipDone = false)
    {
        logger.LogInformation("OCR Job #{Id}: Lade PDF von Paperless (DocId={DocId})", job.Id, job.DocumentId);
        var pdfBytes = await paperless.DownloadDocumentAsync(job.DocumentId, ct);
        logger.LogInformation("OCR Job #{Id}: {Bytes} Bytes heruntergeladen", job.Id, pdfBytes.Length);

        logger.LogInformation("OCR Job #{Id}: Sende an Azure Document Intelligence", job.Id);
        var content = await docIntel.AnalyzeDocumentAsync(pdfBytes, ct);
        logger.LogInformation("OCR Job #{Id}: {Chars} Zeichen extrahiert", job.Id, content.Length);

        // Content in Paperless schreiben
        await paperless.UpdateDocumentAsync(job.DocumentId, new { content }, ct);
        logger.LogInformation("OCR Job #{Id}: Content-PATCH gesendet", job.Id);

        // Verifikation: Wurde der Content tatsächlich gesetzt?
        var docAfter = await paperless.GetDocumentAsync(job.DocumentId, ct);
        var verified = docAfter?.Content == content;
        logger.LogInformation("OCR Job #{Id}: Content-Verifikation: {Result} (Länge vorher={Before}, nachher={After})",
            job.Id,
            verified ? "OK" : "ABWEICHUNG",
            content.Length,
            docAfter?.Content?.Length ?? 0);

        var ocrTagName = settings.Get(PaperlessPollingService.OcrTagKey) ?? PaperlessPollingService.DefaultOcrTagName;
        await RemoveTagAsync(paperless, job.DocumentId, ocrTagName, ct);

        var resultJson = JsonSerializer.Serialize(new OcrResult
        {
            CharCount = content.Length,
            Preview = content.Length > 2000 ? content[..2000] + "…" : content,
            ContentVerified = verified,
            PdfSizeBytes = pdfBytes.Length
        }, CamelCaseOptions);

        if (!skipDone)
            await SetStatus(db, job.Id, JobStatus.Done, ct, resultJson: resultJson);

        logger.LogInformation("OCR Job #{Id} abgeschlossen (verified={V})", job.Id, verified);
    }

    private record OcrResult
    {
        public int CharCount { get; init; }
        public string Preview { get; init; } = string.Empty;
        public bool ContentVerified { get; init; }
        public int PdfSizeBytes { get; init; }
    }

    // ── AI ───────────────────────────────────────────────────────────────────

    private async Task RunAiAsync(
        AppDbContext db, PaperlessClient paperless, OpenAIService openAi,
        MetadataContextBuilder contextBuilder, ProcessingJob job, CancellationToken ct)
    {
        logger.LogInformation("AI Job #{Id}: Lade Dokument von Paperless (DocId={DocId})", job.Id, job.DocumentId);
        var doc = await paperless.GetDocumentAsync(job.DocumentId, ct)
            ?? throw new InvalidOperationException($"Dokument {job.DocumentId} nicht gefunden");

        var content = doc.Content;
        if (string.IsNullOrWhiteSpace(content))
            logger.LogWarning("AI Job #{Id}: Dokument hat keinen Textinhalt – OCR wurde möglicherweise nicht durchgeführt", job.Id);

        var metadataContext = await contextBuilder.BuildAsync(ct);
        logger.LogInformation("AI Job #{Id}: Sende an Azure OpenAI ({ContentLen} Zeichen, {MetaLen} Zeichen Kontext)",
            job.Id, content.Length, metadataContext.Length);

        var ocrTagName = settings.Get(PaperlessPollingService.OcrTagKey) ?? PaperlessPollingService.DefaultOcrTagName;
        var aiTagName = settings.Get(PaperlessPollingService.AiTagKey) ?? PaperlessPollingService.DefaultAiTagName;

        var result = await openAi.ProcessDocumentAsync(
            content,
            metadataContext,
            (query, token) => paperless.SearchDocumentsAsync(query, 5, [ocrTagName, aiTagName], token),
            ct);
        logger.LogInformation(
            "AI Job #{Id}: Titel='{Title}' | Datum={Date} | Korrespondent={Corr} | Typ={DType} | Tags=[{Tags}] | Pfad={Path}",
            job.Id,
            result.Title ?? "–",
            result.Created ?? "–",
            result.CorrespondentId?.ToString() ?? "–",
            result.DocumentTypeId?.ToString() ?? "–",
            string.Join(", ", result.TagIds),
            result.StoragePathId?.ToString() ?? "–");
        logger.LogInformation("AI Job #{Id}: Begründung: {Reason}", job.Id, result.Reasoning ?? "–");

        await ResolveNewEntitiesAsync(paperless, db, result, ct);
        await ApplyAiResultAsync(paperless, doc, job.DocumentId, result, ct);

        await RemoveTagAsync(paperless, job.DocumentId, aiTagName, ct);

        var resultJson = JsonSerializer.Serialize(result, CamelCaseOptions);
        await SetStatus(db, job.Id, JobStatus.Done, ct, resultJson: resultJson);
        logger.LogInformation("AI Job #{Id} abgeschlossen", job.Id);
    }

    // ── Hilfsmethoden ────────────────────────────────────────────────────────

    private async Task ResolveNewEntitiesAsync(
        PaperlessClient paperless, AppDbContext db,
        DocumentProcessingResult result, CancellationToken ct)
    {
        // Korrespondent
        if (!string.IsNullOrWhiteSpace(result.NewCorrespondent))
        {
            if (settings.Get(OpenAIService.CanCreateCorrespondentKey).IsTrue())
            {
                var created = await paperless.CreateCorrespondentAsync(result.NewCorrespondent, ct);
                result.CorrespondentId = created.Id;
                await SyncNewEntryAsync(db, EntityType.Correspondent, created.Id, created.Name, ct);
                logger.LogInformation("KI hat neuen Korrespondenten angelegt: '{Name}' (id={Id})", created.Name, created.Id);
            }
            else logger.LogWarning("KI wollte neuen Korrespondenten '{Name}' anlegen – Berechtigung fehlt", result.NewCorrespondent);
        }

        // Dokumenttyp
        if (!string.IsNullOrWhiteSpace(result.NewDocumentType))
        {
            if (settings.Get(OpenAIService.CanCreateDocumentTypeKey).IsTrue())
            {
                var created = await paperless.CreateDocumentTypeAsync(result.NewDocumentType, ct);
                result.DocumentTypeId = created.Id;
                await SyncNewEntryAsync(db, EntityType.DocumentType, created.Id, created.Name, ct);
                logger.LogInformation("KI hat neuen Dokumenttyp angelegt: '{Name}' (id={Id})", created.Name, created.Id);
            }
            else logger.LogWarning("KI wollte neuen Dokumenttyp '{Name}' anlegen – Berechtigung fehlt", result.NewDocumentType);
        }

        // Tags
        foreach (var tagName in (result.NewTags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (settings.Get(OpenAIService.CanCreateTagKey).IsTrue())
            {
                var created = await paperless.CreateTagAsync(tagName, "#607D8B", ct);
                result.TagIds.Add(created.Id);
                await SyncNewEntryAsync(db, EntityType.Tag, created.Id, created.Name, ct);
                logger.LogInformation("KI hat neuen Tag angelegt: '{Name}' (id={Id})", created.Name, created.Id);
            }
            else
            {
                logger.LogWarning("KI wollte neuen Tag '{Name}' anlegen – Berechtigung fehlt", tagName);
                break;
            }
        }

        // Speicherpfad
        if (!string.IsNullOrWhiteSpace(result.NewStoragePath))
        {
            if (settings.Get(OpenAIService.CanCreateStoragePathKey).IsTrue())
            {
                var created = await paperless.CreateStoragePathAsync(result.NewStoragePath, ct);
                result.StoragePathId = created.Id;
                await SyncNewEntryAsync(db, EntityType.StoragePath, created.Id, created.Name, ct);
                logger.LogInformation("KI hat neuen Speicherpfad angelegt: '{Name}' (id={Id})", created.Name, created.Id);
            }
            else logger.LogWarning("KI wollte neuen Speicherpfad '{Name}' anlegen – Berechtigung fehlt", result.NewStoragePath);
        }
    }

    private static async Task SyncNewEntryAsync(
        AppDbContext db, EntityType type, int entityId, string name, CancellationToken ct)
    {
        var existing = await db.MetadataDescriptions
            .FirstOrDefaultAsync(d => d.EntityType == type && d.EntityId == entityId, ct);
        if (existing is not null) return;

        db.MetadataDescriptions.Add(new Models.Domain.MetadataDescription
        {
            EntityType = type,
            EntityId = entityId,
            Name = name,
            Description = string.Empty,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyAiResultAsync(
        PaperlessClient paperless,
        Models.Paperless.PaperlessDocument doc,
        int documentId,
        DocumentProcessingResult result,
        CancellationToken ct)
    {
        var patch = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(result.Title))
            patch["title"] = result.Title;
        if (!string.IsNullOrWhiteSpace(result.Created) &&
            DateOnly.TryParseExact(result.Created, "yyyy-MM-dd", out _))
            patch["created"] = result.Created;
        if (result.CorrespondentId.HasValue)
            patch["correspondent"] = result.CorrespondentId.Value;
        if (result.DocumentTypeId.HasValue)
            patch["document_type"] = result.DocumentTypeId.Value;
        if (result.StoragePathId.HasValue)
            patch["storage_path"] = result.StoragePathId.Value;

        var tagIds = result.TagIds ?? [];
        if (tagIds.Count > 0)
        {
            var merged = doc.Tags.Union(tagIds).ToList();
            patch["tags"] = merged;
            logger.LogInformation("ApplyAI: Tags-Merge: vorher=[{Before}] + KI=[{Ai}] = nachher=[{After}]",
                string.Join(",", doc.Tags), string.Join(",", tagIds), string.Join(",", merged));
        }

        if (result.CustomFields.Count > 0)
        {
            patch["custom_fields"] = result.CustomFields
                .Select(kv => new { field = int.Parse(kv.Key), value = kv.Value })
                .ToList();
        }

        if (patch.Count > 0)
        {
            logger.LogInformation("ApplyAI: PATCH Dokument {DocId} mit {FieldCount} Feld(ern)", documentId, patch.Count);
            await paperless.UpdateDocumentAsync(documentId, patch, ct);
        }
        else
        {
            logger.LogInformation("ApplyAI: Keine Felder zum Aktualisieren");
        }
    }

    private async Task RemoveTagAsync(
        PaperlessClient paperless, int documentId, string tagName, CancellationToken ct)
    {
        var tag = await paperless.GetTagByNameAsync(tagName, ct);
        if (tag is null)
        {
            logger.LogWarning("RemoveTag: Tag '{Name}' existiert nicht in Paperless", tagName);
            return;
        }

        var doc = await paperless.GetDocumentAsync(documentId, ct);
        if (doc is null)
        {
            logger.LogWarning("RemoveTag: Dokument {DocId} nicht gefunden", documentId);
            return;
        }

        if (!doc.Tags.Contains(tag.Id))
        {
            logger.LogDebug("RemoveTag: Tag '{Name}' ({Id}) ist nicht an Dokument {DocId}", tagName, tag.Id, documentId);
            return;
        }

        var updatedTags = doc.Tags.Where(t => t != tag.Id).ToList();
        logger.LogInformation(
            "RemoveTag: Entferne '{Name}' (id={TagId}) von Dokument {DocId}. Tags vorher: [{Before}] → nachher: [{After}]",
            tagName, tag.Id, documentId,
            string.Join(", ", doc.Tags),
            string.Join(", ", updatedTags));

        await paperless.UpdateDocumentAsync(documentId, new { tags = updatedTags }, ct);
        logger.LogInformation("RemoveTag: Tag '{Name}' erfolgreich entfernt", tagName);
    }

    private static async Task SetStatus(
        AppDbContext db, int jobId, JobStatus status, CancellationToken ct,
        string? error = null, string? resultJson = null)
    {
        var job = await db.ProcessingJobs.FindAsync([jobId], ct);
        if (job is null) return;
        job.Status = status;
        job.UpdatedAt = DateTime.UtcNow;
        if (error is not null) job.Error = error;
        if (resultJson is not null) job.ResultJson = resultJson;
        await db.SaveChangesAsync(ct);
    }

    private static string BuildErrorMessage(Exception ex)
    {
        if (ex is Azure.RequestFailedException rfe)
            return $"Azure Fehler {rfe.Status} ({rfe.ErrorCode ?? "unbekannt"}): {rfe.Message}";

        var parts = new List<string>();
        var current = ex;
        while (current is not null)
        {
            parts.Add(current.Message);
            current = current.InnerException;
        }
        return string.Join(" → ", parts);
    }
}
