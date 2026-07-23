using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.EntityFrameworkCore;
using OpenAI.Chat;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;

namespace PaperlessAI.API.Services;

public class ChatService(
    AppSettingsService settings,
    PaperlessClient paperless,
    AppDbContext db,
    ILogger<ChatService> logger)
{
    private const int MaxToolRounds = 5;

    private const string SystemPrompt = """
        Du bist ein Verwaltungsassistent für das Dokumentenverwaltungssystem Paperless-NGX.
        Du hast Zugriff auf Tools um Entitäten zu verwalten: Korrespondenten, Tags, Dokumententypen, Speicherpfade und Custom Fields.

        Verfügbare Aktionen:
        - Entitäten auflisten (list_entities)
        - Entitäten umbenennen (rename_entity) — benötigt ID, neuen Namen und Typ
        - KI-Beschreibungen aktualisieren (update_description) — Beschreibungen helfen der KI bei der automatischen Zuweisung

        Verhalten:
        - Führe Aktionen direkt aus wenn die Anfrage klar ist
        - Wenn du die ID einer Entität brauchst, liste zuerst die Entitäten des entsprechenden Typs
        - Bestätige durchgeführte Aktionen klar und deutlich
        - Antworte immer auf Deutsch
        - Bei Fehlern erkläre was schiefgelaufen ist
        """;

    public async Task<ChatResponse> SendMessageAsync(
        List<ChatTurn> history,
        string userMessage,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage> { new SystemChatMessage(SystemPrompt) };
        foreach (var turn in history)
            messages.Add(turn.Role == "user"
                ? (ChatMessage)new UserChatMessage(turn.Content)
                : new AssistantChatMessage(turn.Content));
        messages.Add(new UserChatMessage(userMessage));

        var tools = BuildTools();
        var options = new ChatCompletionOptions { Tools = { tools[0], tools[1], tools[2] } };

        var actions = new List<ChatAction>();
        var round = 0;
        string replyText = string.Empty;

        while (round <= MaxToolRounds)
        {
            var response = await BuildClient().CompleteChatAsync(messages, options, ct);
            var completion = response.Value;

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                round++;
                messages.Add(new AssistantChatMessage(completion));

                foreach (var toolCall in completion.ToolCalls)
                {
                    var result = await ExecuteToolAsync(toolCall, actions, ct);
                    messages.Add(new ToolChatMessage(toolCall.Id, result));
                }
                continue;
            }

            replyText = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
            break;
        }

        return new ChatResponse(replyText, actions);
    }

    private async Task<string> ExecuteToolAsync(
        ChatToolCall toolCall,
        List<ChatAction> actions,
        CancellationToken ct)
    {
        try
        {
            var args = JsonDocument.Parse(toolCall.FunctionArguments.ToString()).RootElement;

            return toolCall.FunctionName switch
            {
                "list_entities" => await ListEntitiesAsync(args, ct),
                "rename_entity" => await RenameEntityAsync(args, actions, ct),
                "update_description" => await UpdateDescriptionAsync(args, actions, ct),
                _ => "Unbekanntes Tool"
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fehler beim Ausführen von Tool {Tool}", toolCall.FunctionName);
            return $"Fehler: {ex.Message}";
        }
    }

    private async Task<string> ListEntitiesAsync(JsonElement args, CancellationToken ct)
    {
        var type = args.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var entities = type switch
        {
            "correspondent" => (await paperless.GetCorrespondentsAsync(ct))
                .Select(e => new { e.Id, e.Name }),
            "tag" => (await paperless.GetTagsAsync(ct))
                .Select(e => new { e.Id, e.Name }),
            "document_type" => (await paperless.GetDocumentTypesAsync(ct))
                .Select(e => new { e.Id, e.Name }),
            "storage_path" => (await paperless.GetStoragePathsAsync(ct))
                .Select(e => new { e.Id, e.Name }),
            "custom_field" => (await paperless.GetCustomFieldsAsync(ct))
                .Select(e => new { e.Id, e.Name }),
            _ => Enumerable.Empty<object>().Select(_ => new { Id = 0, Name = "" })
        };

        return JsonSerializer.Serialize(entities.Cast<object>().ToList());
    }

    private async Task<string> RenameEntityAsync(
        JsonElement args, List<ChatAction> actions, CancellationToken ct)
    {
        var type = args.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var id = args.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
        var newName = args.TryGetProperty("new_name", out var n) ? n.GetString() ?? "" : "";

        var description = $"{TypeLabel(type)} ID={id} umbenannt in \"{newName}\"";
        try
        {
            await (type switch
            {
                "correspondent" => paperless.RenameCorrespondentAsync(id, newName, ct),
                "tag" => paperless.RenameTagAsync(id, newName, ct),
                "document_type" => paperless.RenameDocumentTypeAsync(id, newName, ct),
                "storage_path" => paperless.RenameStoragePathAsync(id, newName, ct),
                _ => Task.FromException(new ArgumentException($"Unbekannter Typ: {type}"))
            });

            await UpdateStoredNameAsync(type, id, newName, ct);
            actions.Add(new ChatAction("rename_entity", description, true, null));
            return $"Erfolgreich: {description}";
        }
        catch (Exception ex)
        {
            actions.Add(new ChatAction("rename_entity", description, false, ex.Message));
            return $"Fehler beim Umbenennen: {ex.Message}";
        }
    }

    private async Task<string> UpdateDescriptionAsync(
        JsonElement args, List<ChatAction> actions, CancellationToken ct)
    {
        var type = args.TryGetProperty("entity_type", out var t) ? t.GetString() ?? "" : "";
        var id = args.TryGetProperty("entity_id", out var idEl) ? idEl.GetInt32() : 0;
        var desc = args.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var name = args.TryGetProperty("name", out var n) ? n.GetString() : null;

        var entityType = ParseEntityType(type);
        var actionDesc = $"Beschreibung für {TypeLabel(type)} ID={id} aktualisiert";

        if (entityType is null)
        {
            var err = $"Unbekannter Entitätstyp: {type}";
            actions.Add(new ChatAction("update_description", actionDesc, false, err));
            return err;
        }

        try
        {
            var existing = await db.MetadataDescriptions
                .FirstOrDefaultAsync(e => e.EntityType == entityType.Value && e.EntityId == id, ct);

            if (existing is null)
            {
                existing = new MetadataDescription
                {
                    EntityType = entityType.Value,
                    EntityId = id,
                    Name = name ?? string.Empty,
                    Description = desc,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MetadataDescriptions.Add(existing);
            }
            else
            {
                existing.Description = desc;
                existing.UpdatedAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(name)) existing.Name = name;
            }

            await db.SaveChangesAsync(ct);
            actions.Add(new ChatAction("update_description", actionDesc, true, null));
            return $"Erfolgreich: {actionDesc}";
        }
        catch (Exception ex)
        {
            actions.Add(new ChatAction("update_description", actionDesc, false, ex.Message));
            return $"Fehler: {ex.Message}";
        }
    }

    private async Task UpdateStoredNameAsync(string type, int id, string newName, CancellationToken ct)
    {
        var entityType = ParseEntityType(type);
        if (entityType is null) return;

        var existing = await db.MetadataDescriptions
            .FirstOrDefaultAsync(e => e.EntityType == entityType.Value && e.EntityId == id, ct);
        if (existing is not null)
        {
            existing.Name = newName;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private ChatClient BuildClient()
    {
        var endpoint = settings.Get("Azure:OpenAI:Endpoint")
            ?? throw new InvalidOperationException("Azure:OpenAI:Endpoint not configured.");
        var key = settings.Get("Azure:OpenAI:Key")
            ?? throw new InvalidOperationException("Azure:OpenAI:Key not configured.");
        var deployment = settings.Get("Azure:OpenAI:DeploymentName") ?? "gpt-4o";
        return new AzureOpenAIClient(new Uri(endpoint), new System.ClientModel.ApiKeyCredential(key))
            .GetChatClient(deployment);
    }

    private static ChatTool[] BuildTools() =>
    [
        ChatTool.CreateFunctionTool(
            functionName: "list_entities",
            functionDescription: "Listet alle Entitäten eines bestimmten Typs aus Paperless auf (mit ID und Name).",
            functionParameters: BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "type": {
                      "type": "string",
                      "enum": ["correspondent", "tag", "document_type", "storage_path", "custom_field"],
                      "description": "Typ der Entität"
                    }
                  },
                  "required": ["type"]
                }
                """)),
        ChatTool.CreateFunctionTool(
            functionName: "rename_entity",
            functionDescription: "Benennt eine Entität in Paperless um.",
            functionParameters: BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "type": {
                      "type": "string",
                      "enum": ["correspondent", "tag", "document_type", "storage_path"],
                      "description": "Typ der Entität"
                    },
                    "id": { "type": "integer", "description": "ID der Entität" },
                    "new_name": { "type": "string", "description": "Neuer Name" }
                  },
                  "required": ["type", "id", "new_name"]
                }
                """)),
        ChatTool.CreateFunctionTool(
            functionName: "update_description",
            functionDescription: "Aktualisiert die KI-Beschreibung einer Entität. Beschreibungen helfen der KI bei der automatischen Dokumentenzuweisung.",
            functionParameters: BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "entity_type": {
                      "type": "string",
                      "enum": ["correspondent", "tag", "document_type", "storage_path", "custom_field"],
                      "description": "Typ der Entität"
                    },
                    "entity_id": { "type": "integer", "description": "ID der Entität" },
                    "name": { "type": "string", "description": "Name der Entität (optional, für die Zuordnung)" },
                    "description": { "type": "string", "description": "Neue KI-Beschreibung" }
                  },
                  "required": ["entity_type", "entity_id", "description"]
                }
                """))
    ];

    private static EntityType? ParseEntityType(string type) => type switch
    {
        "correspondent" => EntityType.Correspondent,
        "tag" => EntityType.Tag,
        "document_type" => EntityType.DocumentType,
        "storage_path" => EntityType.StoragePath,
        "custom_field" => EntityType.CustomField,
        _ => null
    };

    private static string TypeLabel(string type) => type switch
    {
        "correspondent" => "Korrespondent",
        "tag" => "Tag",
        "document_type" => "Dokumententyp",
        "storage_path" => "Speicherpfad",
        "custom_field" => "Custom Field",
        _ => type
    };
}

public record ChatRequest(List<ChatTurn> History, string Message);
public record ChatTurn(string Role, string Content);
public record ChatResponse(string Message, List<ChatAction> Actions);
public record ChatAction(string Tool, string Description, bool Success, string? Error);
