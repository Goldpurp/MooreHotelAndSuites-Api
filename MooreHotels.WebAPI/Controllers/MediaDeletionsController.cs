using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.WebAPI.Services;
using MooreHotels.Domain.Entities;
using System.Security.Claims;
using System.Text.Json;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/admin/media-deletions")]
[Authorize(Roles = "Admin,Manager")]
public sealed class MediaDeletionsController : ControllerBase
{
    private readonly MooreHotelsDbContext _db;

    public MediaDeletionsController(MooreHotelsDbContext db) => _db = db;

    [HttpGet("failed")]
    public async Task<ActionResult<IReadOnlyList<MediaDeletionJobDto>>> GetFailed(
        CancellationToken cancellationToken)
    {
        var jobs = await _db.MediaDeletionJobs
            .AsNoTracking()
            .Where(job => job.AttemptCount >= MediaDeletionWorker.MaximumAttempts)
            .OrderBy(job => job.CreatedAtUtc)
            .Take(200)
            .Select(job => new MediaDeletionJobDto(
                job.Id,
                job.PublicId,
                job.SourceType,
                job.SourceId,
                job.AttemptCount,
                job.NextAttemptAtUtc,
                job.LastErrorCode,
                job.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return Ok(jobs);
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        var job = await _db.MediaDeletionJobs.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken);
        if (job is null) return NotFound(new { message = "Media-deletion job not found." });
        if (job.AttemptCount < MediaDeletionWorker.MaximumAttempts)
        {
            return Conflict(new
            {
                message = "This media deletion is still within its automatic retry window."
            });
        }
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actingUserId))
            return Unauthorized();

        var previousAttemptCount = job.AttemptCount;
        job.AttemptCount = 0;
        job.NextAttemptAtUtc = DateTime.UtcNow;
        job.LockId = null;
        job.LockedUntilUtc = null;
        job.LastErrorCode = null;
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = actingUserId,
            Action = "MEDIA_DELETION_REQUEUED",
            EntityType = "MediaDeletionJob",
            EntityId = job.Id.ToString(),
            OldDataJson = JsonSerializer.Serialize(new { AttemptCount = previousAttemptCount }),
            NewDataJson = JsonSerializer.Serialize(new
            {
                AttemptCount = 0,
                RequestId = HttpContext.TraceIdentifier
            }),
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        return Accepted(new { message = "Media deletion has been queued for retry." });
    }
}
