namespace PaperlessAI.API.Services;

/// <summary>
/// Enriches HTTP error responses with the full request URL so error messages are actionable.
/// </summary>
public class DetailedHttpErrorHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var url = request.RequestUri?.ToString() ?? "unknown URL";
            var short_body = body.Length > 300 ? body[..300] + "…" : body;
            throw new HttpRequestException(
                $"{response.StatusCode} ({(int)response.StatusCode}) bei {request.Method} {url} — {short_body}");
        }

        return response;
    }
}
