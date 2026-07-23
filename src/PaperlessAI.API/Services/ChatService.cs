using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.EntityFrameworkCore;
using OpenAI.Chat;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;
using PaperlessAI.API.Queue;

namespace PaperlessAI.API.Services;

public class ChatService(
    AppSettingsService settings,
    PaperlessClient paperless,
    AppDbContext db,
    DocumentProcessingChannel channel,
    ILogger<ChatService> logger)
{
    private const int MaxToolRounds = 8;

    private const string SystemPrompt = """
        Du bist ein Verwaltungsassistent für das Dokumentenverwaltungssystem Paperless-NGX.
        Du sprichst immer Deutsch und bist präzise und hilfreich.

        WICHTIG: Alle schreibenden Aktionen (umbenennen, erstellen, löschen, Dokumente ändern, Jobs neustarten)
        müssen über propose_*-Tools vorgeschlagen werden. Der Nutzer muss sie explizit genehmigen.
        Führe KEINE Änderungen direkt durch — nutze ausschließlich propose_*-Tools für Schreiboperationen.

        Lesende Aktionen (listen, suchen, Statistiken, Diagnose) führst du sofort aus.

        Arbeitsweise bei Batch-Operationen:
        1. Erst Daten lesen (list_entities, search_documents, etc.)
        2. Alle gewünschten Änderungen als propose_*-Aktionen vorschlagen
        3. Dem Nutzer klar erklären was vorgeschlagen wurde

        Bei Umbenennung oder Erstellung: nutze zuerst list_entities um die korrekte ID zu ermitteln.
        Bei Beschreibungen für Entitäten ohne Beschreibung: nutze find_entities_without_description.
        """;

    public async Task<ChatResponse> SendMessageAsync(
        List<ChatTurn> history, string userMessage, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage> { new SystemChatMessage(SystemPrompt) };
        foreach (var turn in history)
            messages.Add(turn.Role == "user"
                ? (ChatMessage)new UserChatMessage(turn.Content)
                : new AssistantChatMessage(turn.Content));
        messages.Add(new UserChatMessage(userMessage));

        var pendingActions = new List<PendingAction>();
        var tools = BuildTools();
        var options = new ChatCompletionOptions();
        foreach (var t in tools) options.Tools.Add(t);

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
                    var result = await ExecuteToolCallAsync(toolCall, pendingActions, ct);
                    messages.Add(new ToolChatMessage(toolCall.Id, result));
                }
                continue;
            }

            replyText = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
            break;
        }

        return new ChatResponse(replyText, [], pendingActions);
    }

    public async Task<List<ChatAction>> ExecuteApprovedActionsAsync(
        List<PendingAction> actions, CancellationToken ct = default)
    {
        var results = new List<ChatAction>();
        foreach (var action in actions)
        {
            try
            {
                await DispatchActionAsync(action, ct);
                results.Add(new ChatAction(action.Type, action.Description, true, null));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fehler bei Aktion {Type}: {Desc}", action.Type, action.Description);
                results.Add(new ChatAction(action.Type, action.Description, false, ex.Message));
            }
        }
        return results;
    }

    // ── Tool Execution ────────────────────────────────────────────────────────

    private async Task<string> ExecuteToolCallAsync(
        ChatToolCall toolCall, List<PendingAction> pending, CancellationToken ct)
    {
        var args = JsonDocument.Parse(toolCall.FunctionArguments.ToString()).RootElement;
        logger.LogDebug("Chat-Tool: {Tool}", toolCall.FunctionName);

        try
        {
            return toolCall.FunctionName switch
            {
                "list_entities" => await ListEntitiesAsync(args, ct),
                "find_entities_without_description" => await FindWithoutDescriptionAsync(args, ct),
                "find_duplicate_entities" => await FindDuplicatesAsync(args, ct),
                "search_documents" => await SearchDocumentsAsync(args, ct),
                "get_statistics" => await GetStatisticsAsync(ct),
                "health_check" => await HealthCheckAsync(ct),
                "list_failed_jobs" => await ListFailedJobsAsync(args, ct),
                "propose_rename_entity" => ProposeRenameEntity(args, pending),
                "propose_create_entity" => ProposeCreateEntity(args, pending),
                "propose_delete_entity" => ProposeDeleteEntity(args, pending),
                "propose_update_description" => ProposeUpdateDescription(args, pending),
                "propose_update_document" => ProposeUpdateDocument(args, pending),
                "propose_retry_jobs" => ProposeRetryJobs(args, pending),
                _ => "Unbekanntes Tool"
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tool-Fehler: {Tool}", toolCall.FunctionName);
            return $"Fehler: {ex.Message}";
        }
    }

    // ── Read Tools ────────────────────────────────────────────────────────────

    private async Task<string> ListEntitiesAsync(JsonElement args, CancellationToken ct)
    {
        var type = args.GetStringProp("type");
        var entities = await FetchEntitiesAsync(type, ct);
        return JsonSerializer.Serialize(entities.Select(e => new { id = e.Id, name = e.Name }));
    }

    private async Task<string> FindWithoutDescriptionAsync(JsonElement args, CancellationToken ct)
    {
        var type = args.GetStringProp("type");
        var entities = await FetchEntitiesAsync(type, ct);
        var entityType = ParseEntityType(type);

        var withDesc = entityType.HasValue
            ? (await db.MetadataDescriptions
                .Where(d => d.EntityType == entityType.Value && d.Description != "")
                .Select(d => d.EntityId)
                .ToListAsync(ct))
                .ToHashSet()
            : new HashSet<int>();

        var missing = entities.Where(e => !withDesc.Contains(e.Id))
            .Select(e => new { id = e.Id, name = e.Name })
            .ToList();
        return JsonSerializer.Serialize(missing);
    }

    private async Task<string> FindDuplicatesAsync(JsonElement args, CancellationToken ct)
    {
        var type = args.GetStringProp("type");
        var entities = await FetchEntitiesAsync(type, ct);

        var groups = entities
            .GroupBy(e => Normalize(e.Name))
            .Where(g => g.Count() > 1)
            .Select(g => new { normalized = g.Key, items = g.Select(e => new { id = e.Id, name = e.Name }).ToList() })
            .ToList();

        return groups.Count == 0
            ? "Keine Duplikate gefunden."
            : JsonSerializer.Serialize(groups);
    }

    private async Task<string> SearchDocumentsAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.GetStringPropOrNull("query");
        var correspondentId = args.GetIntPropOrNull("correspondent_id");
        var documentTypeId = args.GetIntPropOrNull("document_type_id");
        var tagIds = args.TryGetProperty("tag_ids", out var tagEl)
            ? tagEl.EnumerateArray().Select(t => t.GetInt32()).ToList()
            : null;
        var limit = args.GetIntPropOrNull("limit") ?? 20;

        var docs = await paperless.SearchDocumentsAdvancedAsync(
            query, correspondentId, documentTypeId, tagIds, limit, ct);

        var result = docs.Select(d => new
        {
            d.Id, d.Title, d.CreatedDate,
            correspondent_id = d.CorrespondentId,
            document_type_id = d.DocumentTypeId,
            tag_ids = d.Tags,
            storage_path_id = d.StoragePathId
        });
        return JsonSerializer.Serialize(result);
    }

    private async Task<string> GetStatisticsAsync(CancellationToken ct)
    {
        var jobStats = await db.ProcessingJobs
            .GroupBy(j => j.Status)
            .Select(g => new { status = g.Key.ToString(), count = g.Count() })
            .ToListAsync(ct);

        var correspondents = await paperless.GetCorrespondentsAsync(ct);
        var tags = await paperless.GetTagsAsync(ct);
        var docTypes = await paperless.GetDocumentTypesAsync(ct);
        var storagePaths = await paperless.GetStoragePathsAsync(ct);
        var customFields = await paperless.GetCustomFieldsAsync(ct);

        return JsonSerializer.Serialize(new
        {
            jobs = jobStats,
            entities = new
            {
                correspondents = correspondents.Count,
                tags = tags.Count,
                document_types = docTypes.Count,
                storage_paths = storagePaths.Count,
                custom_fields = customFields.Count
            }
        });
    }

    private async Task<string> HealthCheckAsync(CancellationToken ct)
    {
        var paperlessUrl = settings.Get("Paperless:BaseUrl");
        var paperlessToken = settings.Get("Paperless:Token");
        var azureEndpoint = settings.Get("Azure:OpenAI:Endpoint");
        var azureKey = settings.Get("Azure:OpenAI:Key");
        var docIntelEndpoint = settings.Get("Azure:DocumentIntelligence:Endpoint");
        var docIntelKey = settings.Get("Azure:DocumentIntelligence:Key");

        bool paperlessOk = false;
        string paperlessError = string.Empty;
        try
        {
            await paperless.GetTagsAsync(ct);
            paperlessOk = true;
        }
        catch (Exception ex) { paperlessError = ex.Message; }

        return JsonSerializer.Serialize(new
        {
            paperless = new
            {
                url = paperlessUrl ?? "(nicht gesetzt)",
                token_set = !string.IsNullOrEmpty(paperlessToken),
                connected = paperlessOk,
                error = paperlessError.Length > 0 ? paperlessError : null
            },
            azure_openai = new
            {
                endpoint_set = !string.IsNullOrEmpty(azureEndpoint),
                key_set = !string.IsNullOrEmpty(azureKey),
                deployment = settings.Get("Azure:OpenAI:DeploymentName") ?? "gpt-4o (default)"
            },
            azure_document_intelligence = new
            {
                endpoint_set = !string.IsNullOrEmpty(docIntelEndpoint),
                key_set = !string.IsNullOrEmpty(docIntelKey),
                model = settings.Get("Azure:DocumentIntelligence:Model") ?? "(default)"
            }
        });
    }

    private async Task<string> ListFailedJobsAsync(JsonElement args, CancellationToken ct)
    {
        var limit = args.GetIntPropOrNull("limit") ?? 20;
        var jobs = await db.ProcessingJobs
            .Where(j => j.Status == JobStatus.Failed)
            .OrderByDescending(j => j.UpdatedAt)
            .Take(limit)
            .Select(j => new { j.Id, j.DocumentId, j.DocumentTitle, j.JobType, j.Error, j.UpdatedAt })
            .ToListAsync(ct);

        return JsonSerializer.Serialize(jobs);
    }

    // ── Propose Tools (Write — need approval) ─────────────────────────────────

    private string ProposeRenameEntity(JsonElement args, List<PendingAction> pending)
    {
        var type = args.GetStringProp("type");
        var id = args.GetIntProp("id");
        var newName = args.GetStringProp("new_name");
        var currentName = args.GetStringPropOrNull("current_name") ?? $"ID={id}";

        var action = new PendingAction(
            Guid.NewGuid().ToString(),
            "rename_entity",
            $"{TypeLabel(type)} \"{currentName}\" → \"{newName}\"",
            new Dictionary<string, JsonElement>
            {
                ["type"] = JsonDocument.Parse($"\"{type}\"").RootElement,
                ["id"] = JsonDocument.Parse($"{id}").RootElement,
                ["new_name"] = JsonDocument.Parse($"\"{newName}\"").RootElement
            });
        pending.Add(action);
        return $"Aktion vorgeschlagen: {action.Description}";
    }

    private string ProposeCreateEntity(JsonElement args, List<PendingAction> pending)
    {
        var type = args.GetStringProp("type");
        var name = args.GetStringProp("name");
        var description = args.GetStringPropOrNull("description");

        var action = new PendingAction(
            Guid.NewGuid().ToString(),
            "create_entity",
            $"{TypeLabel(type)} \"{name}\" anlegen",
            new Dictionary<string, JsonElement>
            {
                ["type"] = JsonDocument.Parse($"\"{type}\"").RootElement,
                ["name"] = JsonDocument.Parse($"\"{name}\"").RootElement,
                ["description"] = JsonDocument.Parse(description != null ? $"\"{description}\"" : "null").RootElement
            });
        pending.Add(action);
        return $"Aktion vorgeschlagen: {action.Description}";
    }

    private string ProposeDeleteEntity(JsonElement args, List<PendingAction> pending)
    {
        var type = args.GetStringProp("type");
        var id = args.GetIntProp("id");
        var name = args.GetStringPropOrNull("name") ?? $"ID={id}";

        var action = new PendingAction(
            Guid.NewGuid().ToString(),
            "delete_entity",
            $"{TypeLabel(type)} \"{name}\" (ID={id}) löschen",
            new Dictionary<string, JsonElement>
            {
                ["type"] = JsonDocument.Parse($"\"{type}\"").RootElement,
                ["id"] = JsonDocument.Parse($"{id}").RootElement
            });
        pending.Add(action);
        return $"Aktion vorgeschlagen: {action.Description}";
    }

    private string ProposeUpdateDescription(JsonElement args, List<PendingAction> pending)
    {
        var entityType = args.GetStringProp("entity_type");
        var entityId = args.GetIntProp("entity_id");
        var name = args.GetStringPropOrNull("name") ?? $"ID={entityId}";
        var description = args.GetStringProp("description");

        var action = new PendingAction(
            Guid.NewGuid().ToString(),
            "update_description",
            $"Beschreibung für {TypeLabel(entityType)} \"{name}\" setzen",
            new Dictionary<string, JsonElement>
            {
                ["entity_type"] = JsonDocument.Parse($"\"{entityType}\"").RootElement,
                ["entity_id"] = JsonDocument.Parse($"{entityId}").RootElement,
                ["name"] = JsonDocument.Parse($"\"{name}\"").RootElement,
                ["description"] = JsonDocument.Parse(JsonSerializer.Serialize(description)).RootElement
            });
        pending.Add(action);
        return $"Aktion vorgeschlagen: {action.Description}";
    }

    private string ProposeUpdateDocument(JsonElement args, List<PendingAction> pending)
    {
        var docId = args.GetIntProp("document_id");
        var title = args.GetStringPropOrNull("title");
        var currentTitle = args.GetStringPropOrNull("current_title") ?? $"ID={docId}";

        var changes = new List<string>();
        if (title != null) changes.Add($"Titel: \"{title}\"");
        if (args.TryGetProperty("correspondent_id", out _)) changes.Add("Korrespondent");
        if (args.TryGetProperty("document_type_id", out _)) changes.Add("Dokumententyp");
        if (args.TryGetProperty("tag_ids", out _)) changes.Add("Tags");
        if (args.TryGetProperty("storage_path_id", out _)) changes.Add("Speicherpfad");

        var action = new PendingAction(
            Guid.NewGuid().ToString(),
            "update_document",
            $"Dokument \"{currentTitle}\" (ID={docId}): {string.Join(", ", changes)}",
            args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
        pending.Add(action);
        return $"Aktion vorgeschlagen: {action.Description}";
    }

    private string ProposeRetryJobs(JsonElement args, List<PendingAction> pending)
    {
        var all = args.TryGetProperty("all_failed", out var allEl) && allEl.GetBoolean();
        var jobIds = args.TryGetProperty("job_ids", out var idsEl)
            ? idsEl.EnumerateArray().Select(e => e.GetInt32()).ToList()
            : new List<int>();

        var description = all
            ? "Alle fehlgeschlagenen Jobs neu starten"
            : $"{jobIds.Count} Job(s) neu starten (IDs: {string.Join(", ", jobIds)})";

        var action = new PendingAction(
            Guid.NewGuid().ToString(),
            "retry_jobs",
            description,
            new Dictionary<string, JsonElement>
            {
                ["all_failed"] = JsonDocument.Parse(all ? "true" : "false").RootElement,
                ["job_ids"] = JsonDocument.Parse(JsonSerializer.Serialize(jobIds)).RootElement
            });
        pending.Add(action);
        return $"Aktion vorgeschlagen: {action.Description}";
    }

    // ── Dispatch (Execute approved actions) ───────────────────────────────────

    private async Task DispatchActionAsync(PendingAction action, CancellationToken ct)
    {
        var p = action.Parameters;

        switch (action.Type)
        {
            case "rename_entity":
                var renameType = p["type"].GetString()!;
                var renameId = p["id"].GetInt32();
                var newName = p["new_name"].GetString()!;
                await (renameType switch
                {
                    "correspondent" => paperless.RenameCorrespondentAsync(renameId, newName, ct),
                    "tag" => paperless.RenameTagAsync(renameId, newName, ct),
                    "document_type" => paperless.RenameDocumentTypeAsync(renameId, newName, ct),
                    "storage_path" => paperless.RenameStoragePathAsync(renameId, newName, ct),
                    _ => throw new ArgumentException($"Unbekannter Typ: {renameType}")
                });
                await UpdateStoredNameAsync(renameType, renameId, newName, ct);
                break;

            case "create_entity":
                var createType = p["type"].GetString()!;
                var createName = p["name"].GetString()!;
                var createDesc = p.TryGetValue("description", out var descEl) && descEl.ValueKind != JsonValueKind.Null
                    ? descEl.GetString() : null;
                var created = await (createType switch
                {
                    "correspondent" => paperless.CreateCorrespondentAsync(createName, ct).Then(e => (int?)e.Id),
                    "tag" => paperless.CreateTagAsync(createName, "#6B7280", ct).Then(e => (int?)e.Id),
                    "document_type" => paperless.CreateDocumentTypeAsync(createName, ct).Then(e => (int?)e.Id),
                    "storage_path" => paperless.CreateStoragePathAsync(createName, ct).Then(e => (int?)e.Id),
                    "custom_field" => paperless.CreateCustomFieldAsync(createName, "string", ct).Then(e => (int?)e.Id),
                    _ => throw new ArgumentException($"Unbekannter Typ: {createType}")
                });
                if (created.HasValue && !string.IsNullOrEmpty(createDesc))
                    await SaveDescriptionAsync(createType, created.Value, createName, createDesc, ct);
                break;

            case "delete_entity":
                var deleteType = p["type"].GetString()!;
                var deleteId = p["id"].GetInt32();
                var endpoint = deleteType switch
                {
                    "correspondent" => "correspondents",
                    "tag" => "tags",
                    "document_type" => "document_types",
                    "storage_path" => "storage_paths",
                    "custom_field" => "custom_fields",
                    _ => throw new ArgumentException($"Unbekannter Typ: {deleteType}")
                };
                await paperless.DeleteEntityAsync(endpoint, deleteId, ct);
                break;

            case "update_description":
                var descType = p["entity_type"].GetString()!;
                var descId = p["entity_id"].GetInt32();
                var descName = p.TryGetValue("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var descText = p["description"].GetString() ?? "";
                await SaveDescriptionAsync(descType, descId, descName, descText, ct);
                break;

            case "update_document":
                var docId = p["document_id"].GetInt32();
                var patch = new Dictionary<string, object?>();
                if (p.TryGetValue("title", out var titleEl)) patch["title"] = titleEl.GetString();
                if (p.TryGetValue("correspondent_id", out var corrEl))
                    patch["correspondent"] = corrEl.ValueKind == JsonValueKind.Null ? null : (object?)corrEl.GetInt32();
                if (p.TryGetValue("document_type_id", out var dtEl))
                    patch["document_type"] = dtEl.ValueKind == JsonValueKind.Null ? null : (object?)dtEl.GetInt32();
                if (p.TryGetValue("storage_path_id", out var spEl))
                    patch["storage_path"] = spEl.ValueKind == JsonValueKind.Null ? null : (object?)spEl.GetInt32();
                if (p.TryGetValue("tag_ids", out var tagEl))
                    patch["tags"] = tagEl.EnumerateArray().Select(e => e.GetInt32()).ToList();
                await paperless.UpdateDocumentAsync(docId, patch, ct);
                break;

            case "retry_jobs":
                var retryAll = p["all_failed"].GetBoolean();
                var retryIds = retryAll
                    ? await db.ProcessingJobs
                        .Where(j => j.Status == JobStatus.Failed)
                        .Select(j => j.Id)
                        .ToListAsync(ct)
                    : p["job_ids"].EnumerateArray().Select(e => e.GetInt32()).ToList();

                foreach (var jobId in retryIds)
                {
                    var job = await db.ProcessingJobs.FindAsync([jobId], ct);
                    if (job is null) continue;
                    job.Status = JobStatus.Pending;
                    job.Error = null;
                    job.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await channel.Writer.WriteAsync(job, ct);
                }
                break;

            default:
                throw new ArgumentException($"Unbekannter Aktionstyp: {action.Type}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<List<(int Id, string Name)>> FetchEntitiesAsync(string type, CancellationToken ct)
        => type switch
        {
            "correspondent" => (await paperless.GetCorrespondentsAsync(ct)).Select(e => (e.Id, e.Name)).ToList(),
            "tag" => (await paperless.GetTagsAsync(ct)).Select(e => (e.Id, e.Name)).ToList(),
            "document_type" => (await paperless.GetDocumentTypesAsync(ct)).Select(e => (e.Id, e.Name)).ToList(),
            "storage_path" => (await paperless.GetStoragePathsAsync(ct)).Select(e => (e.Id, e.Name)).ToList(),
            "custom_field" => (await paperless.GetCustomFieldsAsync(ct)).Select(e => (e.Id, e.Name)).ToList(),
            _ => []
        };

    private async Task SaveDescriptionAsync(
        string type, int id, string name, string description, CancellationToken ct)
    {
        var entityType = ParseEntityType(type);
        if (entityType is null) return;

        var existing = await db.MetadataDescriptions
            .FirstOrDefaultAsync(e => e.EntityType == entityType.Value && e.EntityId == id, ct);

        if (existing is null)
        {
            db.MetadataDescriptions.Add(new MetadataDescription
            {
                EntityType = entityType.Value, EntityId = id,
                Name = name, Description = description, UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Description = description;
            existing.UpdatedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(name)) existing.Name = name;
        }
        await db.SaveChangesAsync(ct);
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

    private static string Normalize(string name) =>
        name.ToLowerInvariant().Trim().Replace("  ", " ");

    private static ChatTool[] BuildTools() =>
    [
        // ── Read tools ──
        ChatTool.CreateFunctionTool("list_entities",
            "Listet alle Entitäten eines Typs (mit ID und Name).",
            BinaryData.FromString("""{"type":"object","properties":{"type":{"type":"string","enum":["correspondent","tag","document_type","storage_path","custom_field"]}},"required":["type"]}""")),

        ChatTool.CreateFunctionTool("find_entities_without_description",
            "Findet Entitäten eines Typs, die noch keine KI-Beschreibung haben.",
            BinaryData.FromString("""{"type":"object","properties":{"type":{"type":"string","enum":["correspondent","tag","document_type","storage_path","custom_field"]}},"required":["type"]}""")),

        ChatTool.CreateFunctionTool("find_duplicate_entities",
            "Sucht nach Entitäten desselben Typs mit ähnlichen oder identischen Namen.",
            BinaryData.FromString("""{"type":"object","properties":{"type":{"type":"string","enum":["correspondent","tag","document_type","storage_path","custom_field"]}},"required":["type"]}""")),

        ChatTool.CreateFunctionTool("search_documents",
            "Sucht Dokumente in Paperless mit optionalen Filtern.",
            BinaryData.FromString("""{"type":"object","properties":{"query":{"type":"string"},"correspondent_id":{"type":"integer"},"document_type_id":{"type":"integer"},"tag_ids":{"type":"array","items":{"type":"integer"}},"limit":{"type":"integer","default":20}}}""")),

        ChatTool.CreateFunctionTool("get_statistics",
            "Liefert Job-Statistiken und Anzahl der Entitäten.",
            BinaryData.FromString("""{"type":"object","properties":{}}""")),

        ChatTool.CreateFunctionTool("health_check",
            "Prüft die Verbindung zu Paperless und ob Azure-Credentials gesetzt sind.",
            BinaryData.FromString("""{"type":"object","properties":{}}""")),

        ChatTool.CreateFunctionTool("list_failed_jobs",
            "Listet fehlgeschlagene Jobs mit Fehlermeldung.",
            BinaryData.FromString("""{"type":"object","properties":{"limit":{"type":"integer","default":20}}}""")),

        // ── Write/propose tools ──
        ChatTool.CreateFunctionTool("propose_rename_entity",
            "Schlägt vor, eine Entität umzubenennen. NICHT sofort ausgeführt — braucht Genehmigung.",
            BinaryData.FromString("""{"type":"object","properties":{"type":{"type":"string","enum":["correspondent","tag","document_type","storage_path"]},"id":{"type":"integer"},"new_name":{"type":"string"},"current_name":{"type":"string","description":"Aktueller Name für die Anzeige"}},"required":["type","id","new_name"]}""")),

        ChatTool.CreateFunctionTool("propose_create_entity",
            "Schlägt vor, eine neue Entität anzulegen. Braucht Genehmigung.",
            BinaryData.FromString("""{"type":"object","properties":{"type":{"type":"string","enum":["correspondent","tag","document_type","storage_path","custom_field"]},"name":{"type":"string"},"description":{"type":"string"}},"required":["type","name"]}""")),

        ChatTool.CreateFunctionTool("propose_delete_entity",
            "Schlägt vor, eine Entität zu löschen. Braucht Genehmigung.",
            BinaryData.FromString("""{"type":"object","properties":{"type":{"type":"string","enum":["correspondent","tag","document_type","storage_path","custom_field"]},"id":{"type":"integer"},"name":{"type":"string"}},"required":["type","id"]}""")),

        ChatTool.CreateFunctionTool("propose_update_description",
            "Schlägt vor, die KI-Beschreibung einer Entität zu setzen. Braucht Genehmigung.",
            BinaryData.FromString("""{"type":"object","properties":{"entity_type":{"type":"string","enum":["correspondent","tag","document_type","storage_path","custom_field"]},"entity_id":{"type":"integer"},"name":{"type":"string"},"description":{"type":"string"}},"required":["entity_type","entity_id","description"]}""")),

        ChatTool.CreateFunctionTool("propose_update_document",
            "Schlägt vor, ein Dokument zu aktualisieren (Titel, Korrespondent, Typ, Tags, Speicherpfad). Braucht Genehmigung.",
            BinaryData.FromString("""{"type":"object","properties":{"document_id":{"type":"integer"},"current_title":{"type":"string"},"title":{"type":"string"},"correspondent_id":{"type":["integer","null"]},"document_type_id":{"type":["integer","null"]},"storage_path_id":{"type":["integer","null"]},"tag_ids":{"type":"array","items":{"type":"integer"}}},"required":["document_id"]}""")),

        ChatTool.CreateFunctionTool("propose_retry_jobs",
            "Schlägt vor, fehlgeschlagene Jobs neu zu starten. Braucht Genehmigung.",
            BinaryData.FromString("""{"type":"object","properties":{"all_failed":{"type":"boolean","description":"Alle fehlgeschlagenen Jobs neustarten"},"job_ids":{"type":"array","items":{"type":"integer"},"description":"Spezifische Job-IDs"}}}"""))
    ];
}

// ── Extension helpers ──────────────────────────────────────────────────────────

file static class JsonElementExtensions
{
    public static string GetStringProp(this JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

    public static string? GetStringPropOrNull(this JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    public static int GetIntProp(this JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) ? v.GetInt32() : 0;

    public static int? GetIntPropOrNull(this JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetInt32() : null;
}

file static class TaskExtensions
{
    public static async Task<TResult> Then<T, TResult>(this Task<T> task, Func<T, TResult> map)
    {
        var result = await task;
        return map(result);
    }
}

// ── Models ────────────────────────────────────────────────────────────────────

public record ChatRequest(List<ChatTurn> History, string Message);
public record ChatTurn(string Role, string Content);
public record ChatResponse(string Message, List<ChatAction> CompletedActions, List<PendingAction> PendingActions);
public record ChatAction(string Tool, string Description, bool Success, string? Error);
public record PendingAction(string Id, string Type, string Description, Dictionary<string, JsonElement> Parameters);
public record ExecuteRequest(List<PendingAction> Actions);
