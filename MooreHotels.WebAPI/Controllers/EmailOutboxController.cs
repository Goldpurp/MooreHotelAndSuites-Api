using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/operations/email-outbox")]
[Authorize(Roles = "Admin")]
public sealed class EmailOutboxController : ControllerBase
{
    private const int MaximumAttempts = 12;
    private readonly MooreHotelsDbContext _db;

    public EmailOutboxController(MooreHotelsDbContext db) => _db = db;

    [HttpGet("dead-letters")]
    public async Task<IActionResult> GetDeadLetters(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var messages = await _db.EmailOutboxMessages
            .AsNoTracking()
            .Where(message => message.AttemptCount >= MaximumAttempts)
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

        message.AttemptCount = 0;
        message.NextAttemptAtUtc = DateTime.UtcNow;
        message.LockId = null;
        message.LockedUntilUtc = null;
        message.LastErrorCode = null;
        await _db.SaveChangesAsync(cancellationToken);
        return Accepted(new { message.Id, Message = "Delivery was queued for retry." });
    }
}
