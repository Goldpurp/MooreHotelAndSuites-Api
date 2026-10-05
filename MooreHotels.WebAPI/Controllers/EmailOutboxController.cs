using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.Domain.Entities;
using System.Security.Claims;
using System.Text.Json;
using MooreHotels.Application.DTOs;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/operations/email-outbox")]
[Authorize(Roles = "Admin")]
public sealed class EmailOutboxController : ControllerBase
{
    private const int MaximumAttempts = 12;
    private readonly MooreHotelsDbContext _db;

    public EmailOutboxController(MooreHotelsDbContext db) => _db = db;

    [HttpGet("dead-letters/page")]
    public async Task<IActionResult> GetDeadLetterPage([FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 1000000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.EmailOutboxMessages.AsNoTracking()
            .Where(m => m.DeliveredAtUtc == null && m.AttemptCount >= MaximumAttempts);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(m => new RetryQueueItemDto(m.Id, m.Template, "Email", m.AttemptCount,
                m.LastErrorCode, m.CreatedAtUtc, m.QuarantinedAtUtc == null))
            .ToListAsync(cancellationToken);
        return Ok(PagedResult<RetryQueueItemDto>.Create(items, total, page, pageSize));
    }

    [HttpGet("dead-letters")]
    public async Task<IActionResult> GetDeadLetters(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var messages = await _db.EmailOutboxMessages
            .AsNoTracking()
            .Where(message => message.DeliveredAtUtc == null && message.AttemptCount >= MaximumAttempts)
            .OrderByDescending(message => message.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(message => new
            {
                message.Id,
                message.Template,
                message.AttemptCount,
                message.LastErrorCode,
                message.CreatedAtUtc,
                message.NextAttemptAtUtc
            })
            .ToListAsync(cancellationToken);
        return Ok(messages);
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var message = await _db.EmailOutboxMessages
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (message is null) return NotFound();
        if (message.AttemptCount < MaximumAttempts)
        {
            return Conflict(new
            {
                Message = "This email is still within its automatic retry window."
            });
        }
        if (message.QuarantinedAtUtc.HasValue)
        {
            return Conflict(new
            {
                Message = "This dead letter was privacy-scrubbed and cannot be replayed. Re-run the originating business action if a replacement message is still required."
            });
        }
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
            return Unauthorized();

        var previousAttemptCount = message.AttemptCount;
        message.AttemptCount = 0;
        message.NextAttemptAtUtc = DateTime.UtcNow;
        message.LockId = null;
        message.LockedUntilUtc = null;
        message.LastErrorCode = null;
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = actorId,
            Action = "EMAIL_DELIVERY_REQUEUED",
            EntityType = "EmailOutboxMessage",
            EntityId = message.Id.ToString(),
            OldDataJson = JsonSerializer.Serialize(new
            {
                AttemptCount = previousAttemptCount
            }),
            NewDataJson = JsonSerializer.Serialize(new
            {
                AttemptCount = 0,
                RequestId = HttpContext.TraceIdentifier
            }),
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        return Accepted(new { message.Id, Message = "Delivery was queued for retry." });
    }
}
