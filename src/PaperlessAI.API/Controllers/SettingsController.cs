using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;
using PaperlessAI.API.BackgroundServices;
using PaperlessAI.API.Services;

namespace PaperlessAI.API.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController(AppDbContext db) : ControllerBase
{
    private static readonly string[] SettingKeys =
    [
        "Paperless:BaseUrl",
        "Paperless:Token",
        PaperlessPollingService.OcrTagKey,
        PaperlessPollingService.AiTagKey,
        PaperlessPollingService.ErrorTagKey,
        PaperlessPollingService.ReviewTagKey,
        "Azure:DocumentIntelligence:Endpoint",
        DocumentIntelligenceService.OutputFormatKey,
        DocumentIntelligenceService.ModelKey,
        "Azure:DocumentIntelligence:Key",
        "Azure:OpenAI:Endpoint",
        "Azure:OpenAI:Key",
        "Azure:OpenAI:DeploymentName",
        "Polling:IntervalSeconds",
        OpenAIService.SystemPromptKey,
        OpenAIService.UserPromptTemplateKey,
        OpenAIService.CanCreateCorrespondentKey,
        OpenAIService.CanCreateDocumentTypeKey,
        OpenAIService.CanCreateTagKey,
        OpenAIService.CanCreateStoragePathKey,
        OpenAIService.CanCreateCustomFieldKey
    ];

    [HttpGet]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var settings = await db.AppConfigurations.ToListAsync(ct);
        var result = SettingKeys.ToDictionary(
            key => key,
            key => settings.FirstOrDefault(s => s.Key == key)?.Value ?? string.Empty);

        if (string.IsNullOrEmpty(result[OpenAIService.SystemPromptKey]))
            result[OpenAIService.SystemPromptKey] = OpenAIService.DefaultSystemPrompt;
        if (string.IsNullOrEmpty(result[OpenAIService.UserPromptTemplateKey]))
            result[OpenAIService.UserPromptTemplateKey] = OpenAIService.DefaultUserPromptTemplate;
        if (string.IsNullOrEmpty(result[PaperlessPollingService.OcrTagKey]))
            result[PaperlessPollingService.OcrTagKey] = PaperlessPollingService.DefaultOcrTagName;
        if (string.IsNullOrEmpty(result[PaperlessPollingService.AiTagKey]))
            result[PaperlessPollingService.AiTagKey] = PaperlessPollingService.DefaultAiTagName;
        if (string.IsNullOrEmpty(result[PaperlessPollingService.ErrorTagKey]))
            result[PaperlessPollingService.ErrorTagKey] = PaperlessPollingService.DefaultErrorTagName;
        if (string.IsNullOrEmpty(result[PaperlessPollingService.ReviewTagKey]))
            result[PaperlessPollingService.ReviewTagKey] = PaperlessPollingService.DefaultReviewTagName;
        if (string.IsNullOrEmpty(result[DocumentIntelligenceService.OutputFormatKey]))
            result[DocumentIntelligenceService.OutputFormatKey] = "text";
        if (string.IsNullOrEmpty(result[DocumentIntelligenceService.ModelKey]))
            result[DocumentIntelligenceService.ModelKey] = "auto";

        // CanCreate defaults to "false"
        foreach (var key in new[]
        {
            OpenAIService.CanCreateCorrespondentKey,
            OpenAIService.CanCreateDocumentTypeKey,
            OpenAIService.CanCreateTagKey,
            OpenAIService.CanCreateStoragePathKey,
            OpenAIService.CanCreateCustomFieldKey
        })
        {
            if (string.IsNullOrEmpty(result[key])) result[key] = "false";
        }

        return Ok(result);
    }

    [HttpPut]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] Dictionary<string, string> updates, CancellationToken ct)
    {
        var existing = await db.AppConfigurations.ToListAsync(ct);

        foreach (var (key, value) in updates)
        {
            if (!SettingKeys.Contains(key)) continue;

            var config = existing.FirstOrDefault(c => c.Key == key);
            if (config is null)
                db.AppConfigurations.Add(new AppConfiguration { Key = key, Value = value });
            else
                config.Value = value;
        }

        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("reset-prompt/{promptKey}")]
    public async Task<IActionResult> ResetPrompt(string promptKey, CancellationToken ct)
    {
        var validKeys = new[] { OpenAIService.SystemPromptKey, OpenAIService.UserPromptTemplateKey };
        if (!validKeys.Contains(promptKey)) return BadRequest("Unknown prompt key");

        var config = await db.AppConfigurations.FirstOrDefaultAsync(c => c.Key == promptKey, ct);
        if (config is not null)
        {
            db.AppConfigurations.Remove(config);
            await db.SaveChangesAsync(ct);
        }

        var defaultValue = promptKey == OpenAIService.SystemPromptKey
            ? OpenAIService.DefaultSystemPrompt
            : OpenAIService.DefaultUserPromptTemplate;

        return Ok(new { value = defaultValue });
    }
}
