using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.Application.Common;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/operations")]
[Authorize(Policy = HotelAuthorization.OperationsRead)]
public class OperationsController : ControllerBase
{
    private readonly IOperationService _operationService;
    private readonly IBookingRepository _bookingRepo;
    private readonly MooreHotelsDbContext _dbContext;

    public OperationsController(
        IOperationService operationService,
        IBookingRepository bookingRepo,
        MooreHotelsDbContext dbContext)
    {
        _operationService = operationService;
        _bookingRepo = bookingRepo;
        _dbContext = dbContext;
    }

    [HttpGet("ledger")]
    public async Task<IActionResult> GetLedger(
        [FromQuery] string? filter,
        [FromQuery] string? search,
        [FromQuery] int limit = 200,
        [FromQuery] DateTime? beforeUtc = null,
        [FromQuery] Guid? beforeId = null,
        CancellationToken cancellationToken = default)
    {
        if (filter?.Length > 40 || search?.Length > 120)
        {
            return BadRequest(new { Message = "The operations filter or search term is too long." });
        }

        return Ok(await _operationService.GetLedgerAsync(
            filter,
            search,
            limit,
            beforeUtc,
            beforeId,
            cancellationToken));
    }

    [HttpGet("stats/daily")]
    public async Task<IActionResult> GetDailyStats(CancellationToken cancellationToken)
    {
        var todayUtc = DateTime.UtcNow.Date;
        var checkInsToday = await _bookingRepo.GetCheckInsCountAsync(todayUtc, cancellationToken);
        var checkOutsToday = await _bookingRepo.GetCheckOutsCountAsync(todayUtc, cancellationToken);
        var historicalTrace = await _bookingRepo.GetTotalBookingsCountAsync(cancellationToken);

        // This metric describes outbound delivery, not audit-log integrity.
        var unhandledFailures = await _dbContext.EmailOutboxMessages
            .AsNoTracking()
            .CountAsync(m => m.AttemptCount >= 12, cancellationToken);

        return Ok(new
        {
            CheckInsToday = checkInsToday,
            CheckOutsToday = checkOutsToday,
            HistoricalTrace = historicalTrace,
            EmailDeliveryHealth = unhandledFailures == 0 ? "Operational" : "AttentionRequired",
            ExhaustedEmailCount = unhandledFailures
        });
    }
}
