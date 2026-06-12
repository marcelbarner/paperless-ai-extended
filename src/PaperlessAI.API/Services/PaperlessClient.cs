using System.Net.Http.Json;
using System.Text.Json;
using PaperlessAI.API.Models.Paperless;

namespace PaperlessAI.API.Services;

public class PaperlessClient(IHttpClientFactory httpFactory, AppSettingsService settings, ILogger<PaperlessClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private HttpClient BuildClient()
    {
        var baseUrl = settings.Get("Paperless:BaseUrl") ?? "http://localhost:8000";
        var token = settings.Get("Paperless:Token") ?? string.Empty;

        var client = httpFactory.CreateClient("paperless");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/");
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.Add("Authorization", $"Token {token}");
        }
        return client;
    }

    public async Task<List<PaperlessDocument>> GetDocumentsWithTagsAsync(
        IEnumerable<string> tagNames, CancellationToken ct = default)
    {
        var http = BuildClient();
        var query = string.Join(",", tagNames);
        var result = new List<PaperlessDocument>();
        var url = $"documents/?tags__name__in={Uri.EscapeDataString(query)}&page_size=100";

        while (url is not null)
        {
            var page = await http.GetFromJsonAsync<PaperlessPagedResult<PaperlessDocument>>(
                url, JsonOptions, ct);
            if (page is null) break;
            result.AddRange(page.Results);
            url = page.Next is not null
                ? new Uri(page.Next).PathAndQuery.TrimStart('/')
                : null;
        }
        return result;
    }

    public async Task<byte[]> DownloadDocumentAsync(int documentId, CancellationToken ct = default)
        => await BuildClient().GetByteArrayAsync($"documents/{documentId}/download/", ct);

    public async Task<string> SearchDocumentsAsync(string query, int limit = 5, CancellationToken ct = default)
    {
        var url = $"documents/?search={Uri.EscapeDataString(query)}&page_size={limit}&ordering=-created";
        var page = await BuildClient().GetFromJsonAsync<PaperlessPagedResult<PaperlessDocument>>(url, JsonOptions, ct);
        var results = (page?.Results ?? []).Select(d => new
        {
            id = d.Id,
            title = d.Title,
            created_date = d.CreatedDate,
            correspondent_id = d.CorrespondentId,
            document_type_id = d.DocumentTypeId,
            tag_ids = d.Tags,
            storage_path_id = d.StoragePathId
        });
        return System.Text.Json.JsonSerializer.Serialize(results);
    }

    public async Task<PaperlessDocument?> GetDocumentAsync(int documentId, CancellationToken ct = default)
        => await BuildClient().GetFromJsonAsync<PaperlessDocument>($"documents/{documentId}/", JsonOptions, ct);

    public async Task UpdateDocumentAsync(int documentId, object patch, CancellationToken ct = default)
    {
        var http = BuildClient();

        // Explizit serialisieren damit wir sehen was gesendet wird
        var json = System.Text.Json.JsonSerializer.Serialize(patch, JsonOptions);
        logger.LogDebug("PATCH /documents/{Id}/ body: {Json}", documentId, json);

        var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Patch, $"documents/{documentId}/") { Content = content };
        var response = await http.SendAsync(request, ct);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        logger.LogDebug("PATCH /documents/{Id}/ response {Status}: {Body}", documentId, (int)response.StatusCode, responseBody[..Math.Min(200, responseBody.Length)]);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("PATCH /documents/{Id}/ fehlgeschlagen: {Status} {Body}", documentId, response.StatusCode, responseBody);
            throw new HttpRequestException($"{response.StatusCode} ({(int)response.StatusCode}) bei PATCH /documents/{documentId}/ — {responseBody[..Math.Min(300, responseBody.Length)]}");
        }
    }

    public async Task<PaperlessTag?> GetTagByNameAsync(string name, CancellationToken ct = default)
    {
        var page = await BuildClient().GetFromJsonAsync<PaperlessPagedResult<PaperlessTag>>(
            $"tags/?name={Uri.EscapeDataString(name)}", JsonOptions, ct);
        return page?.Results.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<PaperlessTag> CreateTagAsync(string name, string color, CancellationToken ct = default)
        => await PostCreateAsync<PaperlessTag>("tags/", new { name, color }, ct);

    public async Task<PaperlessCorrespondent> CreateCorrespondentAsync(string name, CancellationToken ct = default)
        => await PostCreateAsync<PaperlessCorrespondent>("correspondents/",
            new { name, matching_algorithm = 0 }, ct);

    public async Task<PaperlessDocumentType> CreateDocumentTypeAsync(string name, CancellationToken ct = default)
        => await PostCreateAsync<PaperlessDocumentType>("document_types/",
            new { name, matching_algorithm = 0 }, ct);

    public async Task<PaperlessStoragePath> CreateStoragePathAsync(string name, CancellationToken ct = default)
        => await PostCreateAsync<PaperlessStoragePath>("storage_paths/",
            new { name, path = name }, ct);

    private async Task<T> PostCreateAsync<T>(string endpoint, object body, CancellationToken ct)
    {
        // Explizite Serialisierung ohne Naming-Policy damit Feldnamen wie "name" korrekt bleiben
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        logger.LogDebug("POST {Endpoint} body: {Json}", endpoint, json);
        var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await BuildClient().PostAsync(endpoint, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new System.Net.Http.HttpRequestException(
                $"{response.StatusCode} ({(int)response.StatusCode}) bei POST {endpoint} — {err}");
        }
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct))!;
    }

    public async Task<List<PaperlessCorrespondent>> GetCorrespondentsAsync(CancellationToken ct = default)
        => await GetAllAsync<PaperlessCorrespondent>("correspondents/", ct);

    public async Task<List<PaperlessDocumentType>> GetDocumentTypesAsync(CancellationToken ct = default)
        => await GetAllAsync<PaperlessDocumentType>("document_types/", ct);

    public async Task<List<PaperlessTag>> GetTagsAsync(CancellationToken ct = default)
        => await GetAllAsync<PaperlessTag>("tags/", ct);

    public async Task<List<PaperlessStoragePath>> GetStoragePathsAsync(CancellationToken ct = default)
        => await GetAllAsync<PaperlessStoragePath>("storage_paths/", ct);

    public async Task<List<PaperlessCustomField>> GetCustomFieldsAsync(CancellationToken ct = default)
        => await GetAllAsync<PaperlessCustomField>("custom_fields/", ct);

    private async Task<List<T>> GetAllAsync<T>(string endpoint, CancellationToken ct)
    {
        var http = BuildClient();
        var result = new List<T>();
        string? url = endpoint + "?page_size=200";
        while (url is not null)
        {
            var page = await http.GetFromJsonAsync<PaperlessPagedResult<T>>(url, JsonOptions, ct);
            if (page is null) break;
            result.AddRange(page.Results);
            url = page.Next is not null
                ? new Uri(page.Next).PathAndQuery.TrimStart('/')
                : null;
        }
        return result;
    }
}
