using System.Text.Json;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace PaperlessAI.API.Services;

public class OpenAIService(AppSettingsService settings, ILogger<OpenAIService> logger)
{
    public const string SystemPromptKey = "AI:SystemPrompt";
    public const string UserPromptTemplateKey = "AI:UserPromptTemplate";

    public const string CanCreateCorrespondentKey = "AI:CanCreate:Correspondent";
    public const string CanCreateDocumentTypeKey = "AI:CanCreate:DocumentType";
    public const string CanCreateTagKey = "AI:CanCreate:Tag";
    public const string CanCreateStoragePathKey = "AI:CanCreate:StoragePath";
    public const string CanCreateCustomFieldKey = "AI:CanCreate:CustomField";

    private const int MaxToolRounds = 5;

    public const string DefaultSystemPrompt =
        """
        Du bist ein intelligenter Dokumenten-Assistent für das Dokumentenverwaltungssystem Paperless-NGX.
        Analysiere das Dokument und weise die passenden Metadaten zu.
        Antworte ausschließlich mit validem JSON gemäß dem vorgegebenen Schema.
        Wähle nur aus den angegebenen IDs – erfinde keine neuen Werte, außer wenn die Sektion 'Anlegen-Berechtigungen' es ausdrücklich erlaubt.
        Falls keine passende Option vorhanden ist, setze den Wert auf null.

        Du hast Zugriff auf das Tool 'search_documents', um ähnliche bereits vorhandene Dokumente zu finden.
        Nutze es gezielt um konsistente Benennungen und Metadaten-Zuweisungen zu gewährleisten.
        """;

    public const string DefaultUserPromptTemplate =
        """
        Analysiere folgendes Dokument und bestimme:
        1. Einen aussagekräftigen Titel (title) – präzise, auf Deutsch, max. 80 Zeichen
        2. Das Dokumentdatum (created) – das im Dokument genannte Datum (z.B. Rechnungsdatum, Ausstellungsdatum), Format YYYY-MM-DD. Falls kein Datum erkennbar, null.
        3. Den passenden Korrespondenten (correspondent_id) – oder einen neuen vorschlagen (new_correspondent) wenn erlaubt
        4. Den passenden Dokumenttyp (document_type_id) – oder einen neuen vorschlagen (new_document_type) wenn erlaubt
        5. Passende Tags (tag_ids als Array) – oder neue vorschlagen (new_tags) wenn erlaubt
        6. Den passenden Speicherpfad (storage_path_id) – oder einen neuen vorschlagen (new_storage_path) wenn erlaubt
        7. Werte für Custom Fields (custom_fields als Objekt mit Feld-ID als Schlüssel)
        8. Neue Custom Fields anlegen (new_custom_fields) wenn erlaubt – mit Name, Datentyp und Wert

        Nutze 'search_documents' um ähnliche Dokumente zu finden und Konsistenz zu gewährleisten.

        Verfügbare Metadaten:
        {METADATA_CONTEXT}

        Dokument:
        {DOCUMENT_CONTENT}

        Antworte mit JSON:
        {
          "title": "Aussagekräftiger Titel",
          "created": "YYYY-MM-DD",
          "correspondent_id": null,
          "new_correspondent": null,
          "document_type_id": null,
          "new_document_type": null,
          "tag_ids": [],
          "new_tags": [],
          "storage_path_id": null,
          "new_storage_path": null,
          "custom_fields": {},
          "new_custom_fields": [],
          "new_correspondent_description": null,
          "new_document_type_description": null,
          "new_tag_descriptions": [],
          "new_storage_path_description": null,
          "description_updates": {},
          "reasoning": "Kurze Begründung"
        }
        Regeln:
        - Setze new_* Felder nur wenn in der Sektion 'Anlegen-Berechtigungen' als erlaubt markiert.
        - new_custom_fields: Array von {"name","data_type","value"} Objekten.
        - new_*_description: Beschreibung für neu angelegte Entitäten (Aliases, Zweck, Erkennungsmerkmale).
        - new_tag_descriptions: Parallele Liste zu new_tags, gleiche Reihenfolge.
        - description_updates: Dictionary mit Key "EntityType:ID" (z.B. "Correspondent:3") und neuem Beschreibungstext.
          Nutze dies um Beschreibungen bestehender Entitäten zu ergänzen, z.B. Aliases eines Korrespondenten die im Dokument vorkommen.
          Nur aktualisieren wenn der neue Text wirklich nützlicher ist als der bisherige.
        """;

    public bool CanCreate(string key) =>
        settings.Get(key)?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

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

    public Task<DocumentProcessingResult> ProcessDocumentWithPromptsAsync(
        string documentContent,
        string metadataContext,
        string systemPrompt,
        string userPromptTemplate,
        Func<string, CancellationToken, Task<string>> searchDocuments,
        CancellationToken ct = default)
        => RunAsync(documentContent, metadataContext, systemPrompt, userPromptTemplate, searchDocuments, ct);

    public Task<DocumentProcessingResult> ProcessDocumentAsync(
        string documentContent,
        string metadataContext,
        Func<string, CancellationToken, Task<string>> searchDocuments,
        CancellationToken ct = default)
    {
        var systemPrompt = settings.Get(SystemPromptKey) ?? DefaultSystemPrompt;
        var userTemplate = settings.Get(UserPromptTemplateKey) ?? DefaultUserPromptTemplate;
        return RunAsync(documentContent, metadataContext, systemPrompt, userTemplate, searchDocuments, ct);
    }

    private async Task<DocumentProcessingResult> RunAsync(
        string documentContent,
        string metadataContext,
        string systemPrompt,
        string userTemplate,
        Func<string, CancellationToken, Task<string>> searchDocuments,
        CancellationToken ct)
    {
        logger.LogInformation("Sende Dokument an Azure OpenAI");

        var userPrompt = userTemplate
            .Replace("{METADATA_CONTEXT}", metadataContext)
            .Replace("{DOCUMENT_CONTENT}", documentContent);

        var searchTool = ChatTool.CreateFunctionTool(
            functionName: "search_documents",
            functionDescription: "Sucht ähnliche Dokumente in Paperless-NGX nach Stichwörtern. Gibt Titel, Datum und Metadaten zurück. Nützlich für konsistente Benennung.",
            functionParameters: BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "query": {
                      "type": "string",
                      "description": "Suchbegriff, z.B. Korrespondenten-Name, Dokumenttyp oder Schlagwort aus dem Inhalt"
                    }
                  },
                  "required": ["query"]
                }
                """));

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            Tools = { searchTool }
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        string finalJson = string.Empty;
        var toolRound = 0;
        var toolCallRecords = new List<ToolCallRecord>();

        while (toolRound <= MaxToolRounds)
        {
            var response = await BuildClient().CompleteChatAsync(messages, options, ct);
            var completion = response.Value;

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                toolRound++;
                messages.Add(new AssistantChatMessage(completion));

                foreach (var toolCall in completion.ToolCalls)
                {
                    if (toolCall.FunctionName != "search_documents") continue;

                    var args = JsonDocument.Parse(toolCall.FunctionArguments.ToString());
                    var query = args.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";

                    logger.LogInformation("KI sucht ähnliche Dokumente: '{Query}'", query);
                    var searchResult = await searchDocuments(query, ct);
                    logger.LogInformation("Suchergebnis ({Len} Zeichen): {Result}",
                        searchResult.Length, searchResult.Length > 200 ? searchResult[..200] + "…" : searchResult);

                    toolCallRecords.Add(new ToolCallRecord { Query = query, Result = searchResult });
                    messages.Add(new ToolChatMessage(toolCall.Id, searchResult));
                }
                continue;
            }

            // FinishReason.Stop — finale JSON-Antwort
            finalJson = completion.Content[0].Text;
            break;
        }

        logger.LogInformation("OpenAI Antwort (nach {Rounds} Tool-Runde(n)): {Json}", toolRound, finalJson);

        var result = JsonSerializer.Deserialize<DocumentProcessingResult>(finalJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? new DocumentProcessingResult();

        result.SentSystemPrompt = systemPrompt;
        result.SentUserPrompt = userPrompt;
        result.ToolCalls = toolCallRecords;

        return result;
    }
}

public class DocumentProcessingResult
{
    public string? Title { get; set; }
    public string? Created { get; set; }
    public int? CorrespondentId { get; set; }
    public string? NewCorrespondent { get; set; }
    public string? NewCorrespondentDescription { get; set; }
    public int? DocumentTypeId { get; set; }
    public string? NewDocumentType { get; set; }
    public string? NewDocumentTypeDescription { get; set; }
    public List<int> TagIds { get; set; } = [];
    public List<string> NewTags { get; set; } = [];
    public List<string> NewTagDescriptions { get; set; } = [];
    public int? StoragePathId { get; set; }
    public string? NewStoragePath { get; set; }
    public string? NewStoragePathDescription { get; set; }
    public Dictionary<string, object?> CustomFields { get; set; } = [];
    public List<NewCustomFieldRequest> NewCustomFields { get; set; } = [];

    /// <summary>
    /// Beschreibungs-Updates für bestehende Entitäten.
    /// Key-Format: "Correspondent:3", "DocumentType:2", "Tag:5", "StoragePath:1", "CustomField:4"
    /// Value: neue Beschreibung (z.B. mit Aliases)
    /// </summary>
    public Dictionary<string, string> DescriptionUpdates { get; set; } = [];

    public string? Reasoning { get; set; }
    public string? SentSystemPrompt { get; set; }
    public string? SentUserPrompt { get; set; }
    public List<ToolCallRecord> ToolCalls { get; set; } = [];
}

public class NewCustomFieldRequest
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = "string";
    public object? Value { get; set; }
    public string? Description { get; set; }
}

public class ToolCallRecord
{
    public string Query { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
}
