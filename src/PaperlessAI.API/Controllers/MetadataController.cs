using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;
using PaperlessAI.API.Models.Paperless;
using PaperlessAI.API.Services;

namespace PaperlessAI.API.Controllers;

[ApiController]
[Route("api/metadata/{type}")]
public class MetadataController(AppDbContext db, PaperlessClient paperless) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(string type, CancellationToken ct)
    {
        var entityType = ParseEntityType(type);
        if (entityType is null) return BadRequest("Unknown type");

        var descriptions = await db.MetadataDescriptions
            .Where(d => d.EntityType == entityType.Value)
            .ToListAsync(ct);
        return Ok(descriptions);
    }

    [HttpPut("{id:int}/description")]
    public async Task<IActionResult> UpdateDescription(
        string type, int id, [FromBody] DescriptionUpdateRequest request, CancellationToken ct)
    {
        var entityType = ParseEntityType(type);
        if (entityType is null) return BadRequest("Unknown type");

        var existing = await db.MetadataDescriptions
            .FirstOrDefaultAsync(d => d.EntityType == entityType.Value && d.EntityId == id, ct);

        if (existing is null)
        {
            existing = new MetadataDescription
            {
                EntityType = entityType.Value,
                EntityId = id,
                Name = request.Name ?? string.Empty,
            };
            db.MetadataDescriptions.Add(existing);
        }

        existing.Description = request.Description;
        existing.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(request.Name)) existing.Name = request.Name;

        await db.SaveChangesAsync(ct);
        return Ok(existing);
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(string type, CancellationToken ct)
    {
        var entityType = ParseEntityType(type);
        if (entityType is null) return BadRequest("Unknown type");

        var items = await FetchFromPaperlessAsync(entityType.Value, ct);
        var existing = await db.MetadataDescriptions
            .Where(d => d.EntityType == entityType.Value)
            .ToDictionaryAsync(d => d.EntityId, ct);

        foreach (var (id, name) in items)
        {
            if (!existing.TryGetValue(id, out var desc))
            {
                db.MetadataDescriptions.Add(new MetadataDescription
                {
                    EntityType = entityType.Value,
                    EntityId = id,
                    Name = name,
                    Description = string.Empty,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                desc.Name = name;
                desc.UpdatedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { synced = items.Count });
    }

    private async Task<List<(int Id, string Name)>> FetchFromPaperlessAsync(EntityType type, CancellationToken ct)
        => type switch
        {
            EntityType.Correspondent => (await paperless.GetCorrespondentsAsync(ct))
                .Select(c => (c.Id, c.Name)).ToList(),
            EntityType.DocumentType => (await paperless.GetDocumentTypesAsync(ct))
                .Select(t => (t.Id, t.Name)).ToList(),
            EntityType.Tag => (await paperless.GetTagsAsync(ct))
                .Select(t => (t.Id, t.Name)).ToList(),
            EntityType.StoragePath => (await paperless.GetStoragePathsAsync(ct))
                .Select(s => (s.Id, s.Name)).ToList(),
            EntityType.CustomField => (await paperless.GetCustomFieldsAsync(ct))
                .Select(f => (f.Id, f.Name)).ToList(),
            _ => []
        };

    private static EntityType? ParseEntityType(string type) => type.ToLowerInvariant() switch
    {
        "correspondents" => EntityType.Correspondent,
        "document-types" => EntityType.DocumentType,
        "tags" => EntityType.Tag,
        "storage-paths" => EntityType.StoragePath,
        "custom-fields" => EntityType.CustomField,
        _ => null
    };
}

public record DescriptionUpdateRequest(string? Name, string Description);
