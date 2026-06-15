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
            await HandleFailureTagsAsync(paperless, job, ct);
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
        await ApplyDescriptionUpdatesAsync(db, result, ct);
        await ApplyAiResultAsync(paperless, doc, job.DocumentId, result, ct);

        await RemoveTagAsync(paperless, job.DocumentId, aiTagName, ct);
        await AddReviewTagAsync(paperless, job.DocumentId, ct);

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
                await SyncNewEntryAsync(db, EntityType.Correspondent, created.Id, created.Name, result.NewCorrespondentDescription, ct);
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
                await SyncNewEntryAsync(db, EntityType.DocumentType, created.Id, created.Name, result.NewDocumentTypeDescription, ct);
                logger.LogInformation("KI hat neuen Dokumenttyp angelegt: '{Name}' (id={Id})", created.Name, created.Id);
            }
            else logger.LogWarning("KI wollte neuen Dokumenttyp '{Name}' anlegen – Berechtigung fehlt", result.NewDocumentType);
        }

        // Tags
        var newTags = result.NewTags ?? [];
        var newTagDescs = result.NewTagDescriptions ?? [];
        for (var i = 0; i < newTags.Count; i++)
        {
            var tagName = newTags[i];
            if (string.IsNullOrWhiteSpace(tagName)) continue;
            var tagDesc = i < newTagDescs.Count ? newTagDescs[i] : null;

            if (settings.Get(OpenAIService.CanCreateTagKey).IsTrue())
            {
                var created = await paperless.CreateTagAsync(tagName, "#607D8B", ct);
                result.TagIds.Add(created.Id);
                await SyncNewEntryAsync(db, EntityType.Tag, created.Id, created.Name, tagDesc, ct);
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
                await SyncNewEntryAsync(db, EntityType.StoragePath, created.Id, created.Name, result.NewStoragePathDescription, ct);
                logger.LogInformation("KI hat neuen Speicherpfad angelegt: '{Name}' (id={Id})", created.Name, created.Id);
            }
            else logger.LogWarning("KI wollte neuen Speicherpfad '{Name}' anlegen – Berechtigung fehlt", result.NewStoragePath);
        }

        // Custom Fields
        foreach (var req in (result.NewCustomFields ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Name)))
        {
            if (settings.Get(OpenAIService.CanCreateCustomFieldKey).IsTrue())
            {
                var dataType = req.DataType?.ToLowerInvariant() switch
                {
                    "integer" or "float" or "monetary" or "date" or "boolean" or "url" or "documentlink" => req.DataType.ToLowerInvariant(),
                    _ => "string"
                };
                var created = await paperless.CreateCustomFieldAsync(req.Name, dataType, ct);
                var fieldIdStr = created.Id.ToString();
                if (!result.CustomFields.ContainsKey(fieldIdStr))
                    result.CustomFields[fieldIdStr] = req.Value;
                await SyncNewEntryAsync(db, EntityType.CustomField, created.Id, created.Name, req.Description, ct);
                logger.LogInformation("KI hat neues Custom Field angelegt: '{Name}' (id={Id}, type={Type})",
                    created.Name, created.Id, dataType);
            }
            else logger.LogWarning("KI wollte neues Custom Field '{Name}' anlegen – Berechtigung fehlt", req.Name);
        }
    }

    private static async Task SyncNewEntryAsync(
        AppDbContext db, EntityType type, int entityId, string name, string? description, CancellationToken ct)
    {
        var existing = await db.MetadataDescriptions
            .FirstOrDefaultAsync(d => d.EntityType == type && d.EntityId == entityId, ct);

        if (existing is null)
        {
            db.MetadataDescriptions.Add(new Models.Domain.MetadataDescription
            {
                EntityType = type,
                EntityId = entityId,
                Name = name,
                Description = description ?? string.Empty,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else if (!string.IsNullOrWhiteSpace(description) && existing.Description != description)
        {
            existing.Description = description;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyDescriptionUpdatesAsync(
        AppDbContext db, DocumentProcessingResult result, CancellationToken ct)
    {
        var updates = result.DescriptionUpdates ?? [];
        if (updates.Count == 0) return;

        foreach (var (key, description) in updates)
        {
            if (string.IsNullOrWhiteSpace(description)) continue;

            var parts = key.Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var entityId)) continue;

            var entityType = parts[0] switch
            {
                "Correspondent" => (EntityType?)EntityType.Correspondent,
                "DocumentType"  => EntityType.DocumentType,
                "Tag"           => EntityType.Tag,
                "StoragePath"   => EntityType.StoragePath,
                "CustomField"   => EntityType.CustomField,
                _ => null
            };
            if (entityType is null) continue;

            var entry = await db.MetadataDescriptions
                .FirstOrDefaultAsync(d => d.EntityType == entityType.Value && d.EntityId == entityId, ct);

            if (entry is null)
            {
                logger.LogDebug("DescriptionUpdate: Kein Eintrag für {Key} – überspringe", key);
                continue;
            }

            if (entry.Description == description) continue;

            logger.LogInformation("DescriptionUpdate: {Key} → '{Desc}'", key, description[..Math.Min(80, description.Length)]);
            entry.Description = description;
            entry.UpdatedAt = DateTime.UtcNow;
        }

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

    private async Task AddReviewTagAsync(PaperlessClient paperless, int documentId, CancellationToken ct)
    {
        var reviewTagName = settings.Get(PaperlessPollingService.ReviewTagKey) ?? PaperlessPollingService.DefaultReviewTagName;
        try
        {
            var reviewTag = await paperless.GetTagByNameAsync(reviewTagName, ct);
            if (reviewTag is null) return;

            var doc = await paperless.GetDocumentAsync(documentId, ct);
            if (doc is null || doc.Tags.Contains(reviewTag.Id)) return;

            var updatedTags = doc.Tags.Append(reviewTag.Id).ToList();
            await paperless.UpdateDocumentAsync(documentId, new { tags = updatedTags }, ct);
            logger.LogInformation("Review-Tag '{Tag}' an Dokument {DocId} gesetzt", reviewTagName, documentId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fehler beim Setzen des Review-Tags für Dokument {DocId}", documentId);
        }
    }

    private async Task HandleFailureTagsAsync(
        PaperlessClient paperless, ProcessingJob job, CancellationToken ct)
    {
        try
        {
            var doc = await paperless.GetDocumentAsync(job.DocumentId, ct);
            if (doc is null) return;

            var ocrTagName = settings.Get(PaperlessPollingService.OcrTagKey) ?? PaperlessPollingService.DefaultOcrTagName;
            var aiTagName = settings.Get(PaperlessPollingService.AiTagKey) ?? PaperlessPollingService.DefaultAiTagName;
            var errorTagName = settings.Get(PaperlessPollingService.ErrorTagKey) ?? PaperlessPollingService.DefaultErrorTagName;

            // Automation-Tags auflösen
            var ocrTag = await paperless.GetTagByNameAsync(ocrTagName, ct);
            var aiTag = await paperless.GetTagByNameAsync(aiTagName, ct);
            var errorTag = await paperless.GetTagByNameAsync(errorTagName, ct);

            // Tags berechnen: Automation-Tags entfernen, Error-Tag hinzufügen
            var tagsToRemove = new HashSet<int>();
            if (ocrTag is not null) tagsToRemove.Add(ocrTag.Id);
            if (aiTag is not null) tagsToRemove.Add(aiTag.Id);

            var updatedTags = doc.Tags
                .Where(id => !tagsToRemove.Contains(id))
                .ToList();

            if (errorTag is not null && !updatedTags.Contains(errorTag.Id))
                updatedTags.Add(errorTag.Id);

            await paperless.UpdateDocumentAsync(job.DocumentId, new { tags = updatedTags }, ct);

            logger.LogInformation(
                "Job #{Id} fehlgeschlagen – Automation-Tags entfernt, Fehler-Tag '{Tag}' gesetzt (DocId={DocId})",
                job.Id, errorTagName, job.DocumentId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fehler beim Setzen des Fehler-Tags für Job #{Id}", job.Id);
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
