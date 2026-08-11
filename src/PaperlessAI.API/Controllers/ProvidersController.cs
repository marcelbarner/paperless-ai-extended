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
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, ServiceType = c.ServiceType.ToString(), c.Name, c.Endpoint, HasKey = !string.IsNullOrEmpty(c.ApiKey) })
            .ToListAsync(ct);
        return Ok(connections);
    }

    [HttpPost("connections")]
    public async Task<IActionResult> CreateConnection(
        [FromBody] ConnectionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required");
        if (string.IsNullOrWhiteSpace(req.Endpoint)) return BadRequest("Endpoint is required");
        if (!Enum.TryParse<AiServiceType>(req.ServiceType, out var serviceType))
            return BadRequest($"Invalid serviceType: {req.ServiceType}");

        if (await db.AiConnections.AnyAsync(c => c.Name == req.Name, ct))
            return Conflict($"Connection '{req.Name}' already exists");

        var connection = new AiConnection
        {
            ServiceType = serviceType,
            Name = req.Name.Trim(),
            Endpoint = req.Endpoint.Trim(),
            ApiKey = req.ApiKey ?? string.Empty
        };
        db.AiConnections.Add(connection);
        await db.SaveChangesAsync(ct);
        return Ok(new { connection.Id, ServiceType = connection.ServiceType.ToString(), connection.Name, connection.Endpoint, HasKey = !string.IsNullOrEmpty(connection.ApiKey) });
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
        return Ok(new { connection.Id, ServiceType = connection.ServiceType.ToString(), connection.Name, connection.Endpoint, HasKey = !string.IsNullOrEmpty(connection.ApiKey) });
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
                ConnectionEndpoint = p.Connection.Endpoint,
                p.DeploymentName,
                p.OutputFormat,
                p.AnalysisModel,
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

        var maxPriority = await db.AiProviderConfigs
            .Where(p => p.ServiceType == serviceType)
            .Select(p => (int?)p.Priority)
            .MaxAsync(ct) ?? 0;

        var config = new AiProviderConfig
        {
            ServiceType = serviceType,
            ConnectionId = req.ConnectionId,
            DeploymentName = req.DeploymentName,
            OutputFormat = req.OutputFormat,
            AnalysisModel = req.AnalysisModel,
            Priority = maxPriority + 1,
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

        if (req.ConnectionId > 0) config.ConnectionId = req.ConnectionId;
        config.DeploymentName = req.DeploymentName;
        config.OutputFormat = req.OutputFormat;
        config.AnalysisModel = req.AnalysisModel;
        config.IsEnabled = req.IsEnabled;

        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("configs/reorder")]
    public async Task<IActionResult> ReorderConfigs(
        [FromBody] ReorderRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<AiServiceType>(req.ServiceType, out var serviceType))
            return BadRequest($"Invalid serviceType: {req.ServiceType}");

        var configs = await db.AiProviderConfigs
            .Where(p => p.ServiceType == serviceType)
            .ToListAsync(ct);

        for (var i = 0; i < req.Ids.Count; i++)
        {
            var cfg = configs.FirstOrDefault(c => c.Id == req.Ids[i]);
            if (cfg is not null) cfg.Priority = i + 1;
        }

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

public record ConnectionRequest(string? ServiceType, string Name, string Endpoint, string? ApiKey);
public record ProviderConfigRequest(string? ServiceType, int ConnectionId, string? DeploymentName, string? OutputFormat, string? AnalysisModel, bool IsEnabled);
public record ReorderRequest(string ServiceType, List<int> Ids);
