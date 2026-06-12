using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;
using PaperlessAI.API.Models.Domain;
using PaperlessAI.API.Queue;

namespace PaperlessAI.API.Controllers;

[ApiController]
[Route("api/queue")]
public class QueueController(AppDbContext db, DocumentProcessingChannel channel) : ControllerBase
{
    [HttpGet("jobs")]
    public async Task<IActionResult> GetJobs(
        [FromQuery] JobStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = db.ProcessingJobs.AsQueryable();
        if (status.HasValue) query = query.Where(j => j.Status == status.Value);

        var total = await query.CountAsync(ct);
        var jobs = await query
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, jobs });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var stats = await db.ProcessingJobs
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            .ToListAsync(ct);
        return Ok(stats);
    }

    [HttpPost("jobs/{id:int}/retry")]
    public async Task<IActionResult> RetryJob(int id, CancellationToken ct)
    {
        var job = await db.ProcessingJobs.FindAsync([id], ct);
        if (job is null) return NotFound();
        if (job.Status != JobStatus.Failed) return BadRequest("Only failed jobs can be retried");

        job.Status = JobStatus.Pending;
        job.Error = null;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await channel.Writer.WriteAsync(job, ct);
        return Ok(job);
    }
}
