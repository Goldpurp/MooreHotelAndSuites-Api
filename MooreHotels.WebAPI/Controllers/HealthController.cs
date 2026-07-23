using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Infrastructure.Persistence;

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

            return Ok(new
            {
                Status = "Healthy",
                Timestamp = DateTimeOffset.UtcNow,
                Database = "Connected"
            });
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
}
