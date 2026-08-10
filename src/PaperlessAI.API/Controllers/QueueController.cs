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
        [FromQuery] JobType[]? jobType,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = db.ProcessingJobs.AsQueryable();
        if (status.HasValue) query = query.Where(j => j.Status == status.Value);
        if (jobType is { Length: > 0 }) query = query.Where(j => jobType.Contains(j.JobType));

        var ascending = !string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        query = sortBy switch
        {
            "documentTitle" => ascending ? query.OrderBy(j => j.DocumentTitle) : query.OrderByDescending(j => j.DocumentTitle),
            "jobType"       => ascending ? query.OrderBy(j => j.JobType)       : query.OrderByDescending(j => j.JobType),
            "status"        => ascending ? query.OrderBy(j => j.Status)        : query.OrderByDescending(j => j.Status),
            "createdAt"     => ascending ? query.OrderBy(j => j.CreatedAt)     : query.OrderByDescending(j => j.CreatedAt),
            _               => ascending ? query.OrderBy(j => j.UpdatedAt)     : query.OrderByDescending(j => j.UpdatedAt)
        };

        var total = await query.CountAsync(ct);
        var jobs = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, jobs });
    }

    [HttpPost("jobs/batch-retry")]
    public async Task<IActionResult> BatchRetry(
        [FromBody] List<int> ids, CancellationToken ct)
    {
        if (ids is not { Count: > 0 }) return BadRequest("No job IDs provided");

        var jobs = await db.ProcessingJobs
            .Where(j => ids.Contains(j.Id) && j.Status == JobStatus.Failed)
            .ToListAsync(ct);

        foreach (var job in jobs)
        {
            job.Status = JobStatus.Pending;
            job.Error = null;
            job.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        foreach (var job in jobs)
            await channel.Writer.WriteAsync(job, ct);

        return Ok(new { retried = jobs.Count });
    }

    [HttpPost("jobs/batch-cancel")]
    public async Task<IActionResult> BatchCancel(
        [FromBody] List<int> ids, CancellationToken ct)
    {
        if (ids is not { Count: > 0 }) return BadRequest("No job IDs provided");

        var cancellable = new[] { JobStatus.Pending, JobStatus.Processing };
        var jobs = await db.ProcessingJobs
            .Where(j => ids.Contains(j.Id) && cancellable.Contains(j.Status))
            .ToListAsync(ct);

        foreach (var job in jobs)
        {
            job.Status = JobStatus.Cancelled;
            job.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { cancelled = jobs.Count });
    }

    [HttpDelete("jobs/batch")]
    public async Task<IActionResult> BatchDelete(
        [FromBody] List<int> ids, CancellationToken ct)
    {
        if (ids is not { Count: > 0 }) return BadRequest("No job IDs provided");

        var jobs = await db.ProcessingJobs
            .Where(j => ids.Contains(j.Id))
            .ToListAsync(ct);

        db.ProcessingJobs.RemoveRange(jobs);
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted = jobs.Count });
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
