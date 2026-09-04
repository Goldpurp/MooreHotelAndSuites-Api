using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MooreHotels.WebAPI.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/health")]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    private readonly MooreHotelsDbContext _context;
    public HealthController(MooreHotelsDbContext context) => _context = context;

    [HttpGet]
    public async Task<IActionResult> Check()
    {
        try
        {
            var canConnect = await _context.Database.CanConnectAsync();
            if (!canConnect)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    Status = "Unhealthy",
                    Timestamp = DateTimeOffset.UtcNow,
                    Database = "Disconnected"
                });
            }

            var pendingEmails = await _context.EmailOutboxMessages
                .AsNoTracking()
                .CountAsync(message => message.AttemptCount < 12);
            var exhaustedEmails = await _context.EmailOutboxMessages
                .AsNoTracking()
                .CountAsync(message => message.AttemptCount >= 12);
            var pendingMediaDeletions = await _context.MediaDeletionJobs
                .AsNoTracking()
                .CountAsync(job => job.AttemptCount < MediaDeletionWorker.MaximumAttempts);
            var exhaustedMediaDeletions = await _context.MediaDeletionJobs
                .AsNoTracking()
                .CountAsync(job => job.AttemptCount >= MediaDeletionWorker.MaximumAttempts);

            var response = new
            {
                Status = exhaustedEmails == 0 && exhaustedMediaDeletions == 0
                    ? "Healthy"
                    : "Degraded",
                Timestamp = DateTimeOffset.UtcNow,
                Database = "Connected",
                EmailQueue = new
                {
                    Status = exhaustedEmails == 0 ? "Operational" : "AttentionRequired",
                    Pending = pendingEmails,
                    Exhausted = exhaustedEmails
                },
                MediaDeletionQueue = new
                {
                    Status = exhaustedMediaDeletions == 0 ? "Operational" : "AttentionRequired",
                    Pending = pendingMediaDeletions,
                    Exhausted = exhaustedMediaDeletions
                }
            };

            // Delivery failures require an operational alert, but they must not
            // remove a database-connected API instance from the load balancer.
            return Ok(response);
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Status = "Unhealthy",
                Timestamp = DateTimeOffset.UtcNow,
                Database = "Disconnected"
            });
        }
    }

    [HttpGet("~/health/ready")]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        try
        {
            return await _context.Database.CanConnectAsync(cancellationToken)
                ? Ok(new
                {
                    Status = "Ready",
                    Timestamp = DateTimeOffset.UtcNow,
                    Database = "Connected"
                })
                : StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    Status = "NotReady",
                    Timestamp = DateTimeOffset.UtcNow,
                    Database = "Disconnected"
                });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Status = "NotReady",
                Timestamp = DateTimeOffset.UtcNow,
                Database = "Disconnected"
            });
        }
    }
}
