using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.Application.Services;

public class AnalyticsService : IAnalyticsService
{
    private readonly IBookingRepository _bookingRepo;
    private readonly IRoomRepository _roomRepo;

    public AnalyticsService(IBookingRepository bookingRepo, IRoomRepository roomRepo)
    {
        _bookingRepo = bookingRepo;
        _roomRepo = roomRepo;
    }

    public async Task<DashboardOverviewDto> GetOverviewAsync()
    {
        var now = DateTime.UtcNow;
        var current30DaysStart = now.AddDays(-30);
        var previous30DaysStart = now.AddDays(-60);
        var occupancyWindowEnd = now.Date.AddDays(1);
        var currentOccupancyStart = occupancyWindowEnd.AddDays(-30);
        var previousOccupancyStart = currentOccupancyStart.AddDays(-30);

        // Both repositories share the request-scoped DbContext. EF Core does
        // not permit parallel operations on one context, so each aggregation
        // must complete before the next one begins.
        var netRevenue = await _bookingRepo.GetNetRevenueAsync();
        var priorPeriodRevenue = await _bookingRepo.GetNetRevenueAsync(
            previous30DaysStart,
            current30DaysStart);
        var currentPeriodRevenue = await _bookingRepo.GetNetRevenueAsync(
            current30DaysStart,
            now);
        var activeGuests = await _bookingRepo.GetActiveGuestsCountAsync();
        var avgNightlyRate = await _bookingRepo.GetAverageNightlyRateAsync();
        var revenueDynamics = await _bookingRepo.GetDailyRevenueDynamicsAsync(7);
        var activeOperations = await _bookingRepo.GetActiveOperationsAsync(5);
        var assetStatus = await _roomRepo.GetAssetStatusDistributionAsync();
        var (totalRooms, _) = await _roomRepo.GetRoomCountsAsync();
        var currentOccupiedNights = await _bookingRepo.GetOccupiedRoomNightsAsync(
            currentOccupancyStart,
            occupancyWindowEnd);
        var previousOccupiedNights = await _bookingRepo.GetOccupiedRoomNightsAsync(
            previousOccupancyStart,
            currentOccupancyStart);
        var availableRoomNights = totalRooms * 30d;
        var occupancyRate = availableRoomNights > 0
            ? currentOccupiedNights / availableRoomNights * 100d
            : 0d;
        var previousOccupancyRate = availableRoomNights > 0
            ? previousOccupiedNights / availableRoomNights * 100d
            : 0d;

        // Dynamic revenue growth rate calculation (period over period)
        decimal revenueGrowthPercentage = 0m;
        if (priorPeriodRevenue > 0)
        {
            revenueGrowthPercentage = Math.Round(((currentPeriodRevenue - priorPeriodRevenue) / priorPeriodRevenue) * 100m, 1);
        }
        else if (currentPeriodRevenue > 0)
        {
            revenueGrowthPercentage = 100m;
        }

        var occupancyGrowthPercentage = previousOccupancyRate > 0
            ? Math.Round(
                (occupancyRate - previousOccupancyRate) /
                previousOccupancyRate * 100d,
                1)
            : occupancyRate > 0
                ? 100d
                : 0d;

        var kpis = new DashboardKpis(
            NetRevenue: netRevenue,
            OccupancyRate: Math.Round(occupancyRate, 1),
            ActiveGuests: activeGuests,
            AvgNightlyRate: Math.Round(avgNightlyRate, 2),
            RevenueGrowthPercentage: revenueGrowthPercentage,
            OccupancyGrowthPercentage: occupancyGrowthPercentage
        );

        return new DashboardOverviewDto(
            kpis,
            revenueDynamics.ToList(),
            assetStatus,
            activeOperations.ToList());
    }
}
