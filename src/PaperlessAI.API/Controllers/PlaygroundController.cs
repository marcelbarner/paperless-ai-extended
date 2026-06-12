using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PaperlessAI.API.BackgroundServices;
using PaperlessAI.API.Models.Domain;
using PaperlessAI.API.Services;

namespace PaperlessAI.API.Controllers;

[ApiController]
[Route("api/playground")]
public class PlaygroundController(
    PaperlessClient paperless,
    OpenAIService openAi,
    MetadataContextBuilder contextBuilder,
    AppSettingsService settings,
    ILogger<PlaygroundController> logger) : ControllerBase
{
    // --- Dokumente suchen ---

    [HttpGet("documents")]
    public async Task<IActionResult> SearchDocuments(
        [FromQuery] string? search,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var ocrTag = settings.Get(PaperlessPollingService.OcrTagKey) ?? PaperlessPollingService.DefaultOcrTagName;
        var aiTag = settings.Get(PaperlessPollingService.AiTagKey) ?? PaperlessPollingService.DefaultAiTagName;

        var raw = await paperless.SearchDocumentsForPlaygroundAsync(search, pageSize, [ocrTag, aiTag], ct);
        return Ok(raw);
    }

    // --- Prompt-Vorschau (aufgelöst, ohne KI-Call) ---

    [HttpPost("preview-prompt")]
    public async Task<IActionResult> PreviewPrompt([FromBody] PlaygroundRunRequest request, CancellationToken ct)
    {
        var doc = await paperless.GetDocumentAsync(request.DocumentId, ct);
        if (doc is null) return NotFound($"Dokument {request.DocumentId} nicht gefunden");

        var metadataContext = await contextBuilder.BuildAsync(ct);
        var resolvedUser = request.UserPromptTemplate
            .Replace("{METADATA_CONTEXT}", metadataContext)
            .Replace("{DOCUMENT_CONTENT}", doc.Content);

        return Ok(new
        {
            systemPrompt = request.SystemPrompt,
            userPrompt = resolvedUser,
            charCount = new
            {
                system = request.SystemPrompt.Length,
                user = resolvedUser.Length,
                total = request.SystemPrompt.Length + resolvedUser.Length
            }
        });
    }

    // --- Playground-Run (nicht auf Paperless anwenden) ---

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] PlaygroundRunRequest request, CancellationToken ct)
    {
        logger.LogInformation("Playground-Run für Dokument {DocId}", request.DocumentId);

        var doc = await paperless.GetDocumentAsync(request.DocumentId, ct);
        if (doc is null) return NotFound($"Dokument {request.DocumentId} nicht gefunden");

        var content = doc.Content;
        if (string.IsNullOrWhiteSpace(content))
            logger.LogWarning("Dokument {DocId} hat keinen Textinhalt", request.DocumentId);

        var metadataContext = await contextBuilder.BuildAsync(ct);

        // Temporär die Prompts aus dem Request verwenden
        var ocrTag = settings.Get(PaperlessPollingService.OcrTagKey) ?? PaperlessPollingService.DefaultOcrTagName;
        var aiTag = settings.Get(PaperlessPollingService.AiTagKey) ?? PaperlessPollingService.DefaultAiTagName;

        var result = await openAi.ProcessDocumentWithPromptsAsync(
            content,
            metadataContext,
            request.SystemPrompt,
            request.UserPromptTemplate,
            (query, token) => paperless.SearchDocumentsAsync(query, 5, [ocrTag, aiTag], token),
            ct);

        return Ok(result);
    }

    // --- Ergebnis auf Paperless anwenden ---

    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] PlaygroundApplyRequest request, CancellationToken ct)
    {
        logger.LogInformation("Playground-Apply für Dokument {DocId}", request.DocumentId);

        var doc = await paperless.GetDocumentAsync(request.DocumentId, ct);
        if (doc is null) return NotFound($"Dokument {request.DocumentId} nicht gefunden");

        var patch = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.Title))       patch["title"] = request.Title;
        if (!string.IsNullOrWhiteSpace(request.Created) &&
            DateOnly.TryParseExact(request.Created, "yyyy-MM-dd", out _))
                                                             patch["created"] = request.Created;
        if (request.CorrespondentId.HasValue)                patch["correspondent"] = request.CorrespondentId.Value;
        if (request.DocumentTypeId.HasValue)                 patch["document_type"] = request.DocumentTypeId.Value;
        if (request.StoragePathId.HasValue)                  patch["storage_path"] = request.StoragePathId.Value;

        if (request.TagIds?.Count > 0)
        {
            var merged = doc.Tags.Union(request.TagIds).ToList();
            patch["tags"] = merged;
        }

        if (request.CustomFields?.Count > 0)
        {
            patch["custom_fields"] = request.CustomFields
                .Select(kv => new { field = int.Parse(kv.Key), value = kv.Value })
                .ToList();
        }

        if (patch.Count == 0) return BadRequest("Keine Felder zum Anwenden");

        await paperless.UpdateDocumentAsync(request.DocumentId, patch, ct);
        logger.LogInformation("Playground-Apply für Dokument {DocId} abgeschlossen ({Fields} Felder)", request.DocumentId, patch.Count);

        return Ok();
    }

    // --- Prompts persistieren ---

    [HttpPost("save-prompts")]
    public async Task<IActionResult> SavePrompts([FromBody] SavePromptsRequest request, CancellationToken ct)
    {
        var updates = new Dictionary<string, string>();
        if (request.SystemPrompt is not null) updates[OpenAIService.SystemPromptKey] = request.SystemPrompt;
        if (request.UserPromptTemplate is not null) updates[OpenAIService.UserPromptTemplateKey] = request.UserPromptTemplate;

        if (updates.Count == 0) return BadRequest("Keine Prompts angegeben");

        foreach (var (key, value) in updates)
            await settings.SetAsync(key, value, ct);

        return Ok();
    }
}

public record PlaygroundRunRequest(
    int DocumentId,
    string SystemPrompt,
    string UserPromptTemplate);

public record PlaygroundApplyRequest(
    int DocumentId,
    string? Title,
    string? Created,
    int? CorrespondentId,
    int? DocumentTypeId,
    List<int>? TagIds,
    int? StoragePathId,
    Dictionary<string, object?>? CustomFields);

public record SavePromptsRequest(
    string? SystemPrompt,
    string? UserPromptTemplate);
