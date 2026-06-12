using System.Text;
using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.BackgroundServices;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;

namespace PaperlessAI.API.Services;

/// <summary>
/// Baut den Metadaten-Kontext für den AI-Prompt.
/// Entitäten werden IMMER live von Paperless geladen – die lokale DB liefert nur die Beschreibungen.
/// Damit kennt die KI immer alle aktuellen Einträge, auch wenn sie nicht manuell gesynct wurden.
/// </summary>
public class MetadataContextBuilder(
    AppDbContext db,
    PaperlessClient paperless,
    AppSettingsService settings)
{
    public async Task<string> BuildAsync(CancellationToken ct = default)
    {
        // Beschreibungen aus lokaler DB (für Kontext-Texte)
        var descriptions = await db.MetadataDescriptions
            .AsNoTracking()
            .ToListAsync(ct);

        var descMap = descriptions.ToDictionary(
            d => (d.EntityType, d.EntityId),
            d => d.Description);

        // Automation-Tags aus dem Kontext herausfiltern
        var ocrTagName = settings.Get(PaperlessPollingService.OcrTagKey) ?? PaperlessPollingService.DefaultOcrTagName;
        var aiTagName = settings.Get(PaperlessPollingService.AiTagKey) ?? PaperlessPollingService.DefaultAiTagName;
        var automationTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ocrTagName, aiTagName };

        // Alle Entitäten live von Paperless laden (parallel)
        var (correspondents, docTypes, tags, storagePaths, customFields) = await LoadAllAsync(ct);

        var sb = new StringBuilder();

        AppendLiveSection(sb, "Korrespondenten", EntityType.Correspondent,
            correspondents.Select(c => (c.Id, c.Name)), descMap);

        AppendLiveSection(sb, "Dokumenttypen", EntityType.DocumentType,
            docTypes.Select(t => (t.Id, t.Name)), descMap);

        AppendLiveSection(sb, "Tags", EntityType.Tag,
            tags.Where(t => !automationTagNames.Contains(t.Name)).Select(t => (t.Id, t.Name)), descMap);

        AppendLiveSection(sb, "Speicherpfade", EntityType.StoragePath,
            storagePaths.Select(s => (s.Id, s.Name)), descMap);

        AppendLiveSection(sb, "Custom Fields", EntityType.CustomField,
            customFields.Select(f => (f.Id, f.Name)), descMap);

        AppendPermissions(sb);

        return sb.ToString();
    }

    private async Task<(
        List<Models.Paperless.PaperlessCorrespondent>,
        List<Models.Paperless.PaperlessDocumentType>,
        List<Models.Paperless.PaperlessTag>,
        List<Models.Paperless.PaperlessStoragePath>,
        List<Models.Paperless.PaperlessCustomField>)>
        LoadAllAsync(CancellationToken ct)
    {
        var correspondentsTask = paperless.GetCorrespondentsAsync(ct);
        var docTypesTask = paperless.GetDocumentTypesAsync(ct);
        var tagsTask = paperless.GetTagsAsync(ct);
        var storagePathsTask = paperless.GetStoragePathsAsync(ct);
        var customFieldsTask = paperless.GetCustomFieldsAsync(ct);

        await Task.WhenAll(correspondentsTask, docTypesTask, tagsTask, storagePathsTask, customFieldsTask);

        return (
            await correspondentsTask,
            await docTypesTask,
            await tagsTask,
            await storagePathsTask,
            await customFieldsTask
        );
    }

    private static void AppendLiveSection(
        StringBuilder sb,
        string title,
        EntityType type,
        IEnumerable<(int Id, string Name)> items,
        Dictionary<(EntityType, int), string> descMap)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        sb.AppendLine($"### {title}");
        foreach (var (id, name) in list)
        {
            sb.Append($"- ID {id}: {name}");
            if (descMap.TryGetValue((type, id), out var desc) && !string.IsNullOrWhiteSpace(desc))
                sb.Append($" — {desc}");
            sb.AppendLine();
        }
        sb.AppendLine();
    }

    private void AppendPermissions(StringBuilder sb)
    {
        bool canCorrespondent = IsEnabled(OpenAIService.CanCreateCorrespondentKey);
        bool canDocType = IsEnabled(OpenAIService.CanCreateDocumentTypeKey);
        bool canTag = IsEnabled(OpenAIService.CanCreateTagKey);
        bool canPath = IsEnabled(OpenAIService.CanCreateStoragePathKey);

        var allowed = new List<string>();
        var forbidden = new List<string>();

        Check(canCorrespondent, "Korrespondenten (new_correspondent = \"Name\")", "Korrespondenten", allowed, forbidden);
        Check(canDocType, "Dokumenttypen (new_document_type = \"Name\")", "Dokumenttypen", allowed, forbidden);
        Check(canTag, "Tags (new_tags = [\"Name1\", \"Name2\"])", "Tags", allowed, forbidden);
        Check(canPath, "Speicherpfade (new_storage_path = \"Name\")", "Speicherpfade", allowed, forbidden);

        if (allowed.Count == 0 && forbidden.Count == 0) return;

        sb.AppendLine("### Anlegen-Berechtigungen");

        if (allowed.Count > 0)
        {
            sb.AppendLine("Du darfst folgende neue Einträge anlegen, wenn kein passender in der Liste vorhanden ist:");
            foreach (var a in allowed) sb.AppendLine($"- {a}");
        }

        if (forbidden.Count > 0)
        {
            sb.AppendLine("Folgendes darfst du NICHT neu anlegen – setze new_* auf null und wähle nur aus der Liste:");
            foreach (var f in forbidden) sb.AppendLine($"- {f}");
        }

        sb.AppendLine();
    }

    private bool IsEnabled(string key) =>
        settings.Get(key)?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    private static void Check(bool enabled, string allowedText, string forbiddenText,
        List<string> allowed, List<string> forbidden)
    {
        if (enabled) allowed.Add(allowedText);
        else forbidden.Add(forbiddenText);
    }
}
