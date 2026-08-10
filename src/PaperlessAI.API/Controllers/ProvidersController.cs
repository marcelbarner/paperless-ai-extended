using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;

namespace PaperlessAI.API.Controllers;

[ApiController]
[Route("api/providers")]
public class ProvidersController(AppDbContext db) : ControllerBase
{
    // ── Connections ──────────────────────────────────────────────────────────

    [HttpGet("connections")]
    public async Task<IActionResult> GetConnections(CancellationToken ct)
    {
        var connections = await db.AiConnections
            .Select(c => new { c.Id, c.Name, c.Endpoint, HasKey = !string.IsNullOrEmpty(c.ApiKey) })
            .ToListAsync(ct);
        return Ok(connections);
    }

    [HttpPost("connections")]
    public async Task<IActionResult> CreateConnection(
        [FromBody] ConnectionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required");
        if (string.IsNullOrWhiteSpace(req.Endpoint)) return BadRequest("Endpoint is required");

        if (await db.AiConnections.AnyAsync(c => c.Name == req.Name, ct))
            return Conflict($"Connection '{req.Name}' already exists");

        var connection = new AiConnection
        {
            Name = req.Name.Trim(),
            Endpoint = req.Endpoint.Trim(),
            ApiKey = req.ApiKey ?? string.Empty
        };
        db.AiConnections.Add(connection);
        await db.SaveChangesAsync(ct);
        return Ok(new { connection.Id, connection.Name, connection.Endpoint, HasKey = !string.IsNullOrEmpty(connection.ApiKey) });
    }

    [HttpPut("connections/{id:int}")]
    public async Task<IActionResult> UpdateConnection(
        int id, [FromBody] ConnectionRequest req, CancellationToken ct)
    {
        var connection = await db.AiConnections.FindAsync([id], ct);
        if (connection is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.Name)) connection.Name = req.Name.Trim();
        if (!string.IsNullOrWhiteSpace(req.Endpoint)) connection.Endpoint = req.Endpoint.Trim();
        if (req.ApiKey is not null) connection.ApiKey = req.ApiKey;

        await db.SaveChangesAsync(ct);
        return Ok(new { connection.Id, connection.Name, connection.Endpoint, HasKey = !string.IsNullOrEmpty(connection.ApiKey) });
    }

    [HttpDelete("connections/{id:int}")]
    public async Task<IActionResult> DeleteConnection(int id, CancellationToken ct)
    {
        var connection = await db.AiConnections.FindAsync([id], ct);
        if (connection is null) return NotFound();
        db.AiConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Provider Configs ─────────────────────────────────────────────────────

    [HttpGet("configs")]
    public async Task<IActionResult> GetConfigs(CancellationToken ct)
    {
        var configs = await db.AiProviderConfigs
            .Include(p => p.Connection)
            .OrderBy(p => p.ServiceType)
            .ThenBy(p => p.Priority)
            .Select(p => new
            {
                p.Id,
                ServiceType = p.ServiceType.ToString(),
                p.ConnectionId,
                ConnectionName = p.Connection.Name,
                p.ModelOverride,
                p.Priority,
                p.IsEnabled
            })
            .ToListAsync(ct);
        return Ok(configs);
    }

    [HttpPost("configs")]
    public async Task<IActionResult> CreateConfig(
        [FromBody] ProviderConfigRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<AiServiceType>(req.ServiceType, out var serviceType))
            return BadRequest($"Invalid serviceType: {req.ServiceType}");
        if (!await db.AiConnections.AnyAsync(c => c.Id == req.ConnectionId, ct))
            return BadRequest($"Connection {req.ConnectionId} not found");

        var config = new AiProviderConfig
        {
            ServiceType = serviceType,
            ConnectionId = req.ConnectionId,
            ModelOverride = req.ModelOverride,
            Priority = req.Priority,
            IsEnabled = req.IsEnabled
        };
        db.AiProviderConfigs.Add(config);
        await db.SaveChangesAsync(ct);
        return Ok(config.Id);
    }

    [HttpPut("configs/{id:int}")]
    public async Task<IActionResult> UpdateConfig(
        int id, [FromBody] ProviderConfigRequest req, CancellationToken ct)
    {
        var config = await db.AiProviderConfigs.FindAsync([id], ct);
        if (config is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.ServiceType) &&
            Enum.TryParse<AiServiceType>(req.ServiceType, out var serviceType))
            config.ServiceType = serviceType;
        if (req.ConnectionId > 0) config.ConnectionId = req.ConnectionId;
        config.ModelOverride = req.ModelOverride;
        config.Priority = req.Priority;
        config.IsEnabled = req.IsEnabled;

        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpDelete("configs/{id:int}")]
    public async Task<IActionResult> DeleteConfig(int id, CancellationToken ct)
    {
        var config = await db.AiProviderConfigs.FindAsync([id], ct);
        if (config is null) return NotFound();
        db.AiProviderConfigs.Remove(config);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record ConnectionRequest(string Name, string Endpoint, string? ApiKey);
public record ProviderConfigRequest(string ServiceType, int ConnectionId, string? ModelOverride, int Priority, bool IsEnabled);
