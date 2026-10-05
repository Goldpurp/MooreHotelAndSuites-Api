using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/analytics")]
[Authorize(Roles = "Admin,Manager")]
public class AnalyticsController : ControllerBase
{
    private readonly IAnalyticsService _analyticsService;
    public AnalyticsController(IAnalyticsService analyticsService) => _analyticsService = analyticsService;

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview([FromQuery] string period = "month") => Ok(await _analyticsService.GetOverviewAsync(period));
}