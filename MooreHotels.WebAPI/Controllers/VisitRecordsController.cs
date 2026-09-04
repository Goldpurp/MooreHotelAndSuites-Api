using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/visit-records")]
[Authorize(Roles = "Admin,Manager,Staff")]
public class VisitRecordsController : ControllerBase
{
    private readonly IVisitRecordService _visitService;
    public VisitRecordsController(IVisitRecordService visitService) => _visitService = visitService;

    [HttpGet]
    public async Task<IActionResult> GetRecords(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null) =>
        Ok(await _visitService.GetPagedRecordsAsync(page, pageSize, search));

}
