using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;

namespace PaperlessAI.API.Services;

/// <summary>
/// Reads configuration from the DB (AppConfiguration table), falling back to appsettings.json.
/// DB values always win — they are set via the /api/settings endpoint.
/// </summary>
public class AppSettingsService(IServiceScopeFactory scopeFactory, IConfiguration fallback)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entry = await db.AppConfigurations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry is not null && !string.IsNullOrWhiteSpace(entry.Value))
            return entry.Value;

        return fallback[key];
    }

    public string? Get(string key)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entry = db.AppConfigurations.AsNoTracking()
            .FirstOrDefault(c => c.Key == key);

        if (entry is not null && !string.IsNullOrWhiteSpace(entry.Value))
            return entry.Value;

        return fallback[key];
    }
}
