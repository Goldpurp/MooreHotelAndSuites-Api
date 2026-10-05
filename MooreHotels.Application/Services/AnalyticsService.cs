using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.Application.Services;

public class AnalyticsService : IAnalyticsService
{
    private readonly IBookingRepository _bookingRepo;
    private readonly IRoomRepository _roomRepo;
    private readonly IOperationalReportingService _operationalReporting;
    private readonly IHotelTimeService _hotelTime;

    public AnalyticsService(
        IBookingRepository bookingRepo,
        IRoomRepository roomRepo,
        IOperationalReportingService operationalReporting,
        IHotelTimeService hotelTime)
    {
        _bookingRepo = bookingRepo;
        _roomRepo = roomRepo;
        _operationalReporting = operationalReporting;
        _hotelTime = hotelTime;
    }

    public async Task<DashboardOverviewDto> GetOverviewAsync(string period = "month")
        => await GetAccountingGradeOverviewAsync(period);

    private async Task<DashboardOverviewDto> GetAccountingGradeOverviewAsync(string period)
    {
        var today = _hotelTime.Today;
        var days = period.ToLowerInvariant() switch
        {
            "day" => 1,
            "week" => 7,
            "month" => 30,
            _ => throw new BadRequestException("Choose day, week, or month for the reporting period.")
        };
        var currentFrom = today.AddDays(1 - days);
        var previousFrom = currentFrom.AddDays(-days);
        var current = await _operationalReporting.GetReportAsync(currentFrom, today);
        var previous = await _operationalReporting.GetReportAsync(previousFrom, currentFrom.AddDays(-1));
        var currentNetReceipts = current.Payments - current.Refunds;
        var priorNetReceipts = previous.Payments - previous.Refunds;
        var currentOccupancy = current.AvailableRoomNights == 0 ? 0d :
            current.OccupiedRoomNights / (double)current.AvailableRoomNights * 100d;
        var priorOccupancy = previous.AvailableRoomNights == 0 ? 0d :
            previous.OccupiedRoomNights / (double)previous.AvailableRoomNights * 100d;
        var revenueGrowth = priorNetReceipts != 0
            ? Math.Round((currentNetReceipts - priorNetReceipts) / Math.Abs(priorNetReceipts) * 100m, 1)
            : currentNetReceipts > 0 ? 100m : 0m;
        var occupancyGrowth = priorOccupancy != 0
            ? Math.Round((currentOccupancy - priorOccupancy) / priorOccupancy * 100d, 1)
            : currentOccupancy > 0 ? 100d : 0d;
        var assetStatus = await _roomRepo.GetAssetStatusDistributionAsync();
        var activeOperations = await _bookingRepo.GetActiveOperationsAsync(5);
        var revenueDynamics = await _bookingRepo.GetDailyRevenueDynamicsAsync(days);
        return new DashboardOverviewDto(
            new DashboardKpis(
                currentNetReceipts,
                Math.Round(currentOccupancy, 1),
                await _bookingRepo.GetActiveGuestsCountAsync(),
                current.Adr,
                revenueGrowth,
                occupancyGrowth),
            revenueDynamics.ToList(),
            assetStatus,
            activeOperations.ToList(), currentFrom, today, current);
    }
}
