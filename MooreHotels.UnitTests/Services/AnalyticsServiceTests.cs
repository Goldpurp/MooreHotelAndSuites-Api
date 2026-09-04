using FluentAssertions;
using Moq;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Services;
using Xunit;

namespace MooreHotels.UnitTests.Services;

public class AnalyticsServiceTests
{
    private readonly Mock<IBookingRepository> _bookingRepoMock;
    private readonly Mock<IRoomRepository> _roomRepoMock;
    private readonly AnalyticsService _service;

    public AnalyticsServiceTests()
    {
        _bookingRepoMock = new Mock<IBookingRepository>();
        _roomRepoMock = new Mock<IRoomRepository>();
        _service = new AnalyticsService(_bookingRepoMock.Object, _roomRepoMock.Object);
    }

    [Fact]
    public async Task GetOverviewAsync_CalculatesPositiveRevenueGrowth_Correctly()
    {
        // Arrange
        _bookingRepoMock.Setup(b => b.GetNetRevenueAsync(null, null, default)).ReturnsAsync(1500000m);
        _bookingRepoMock.Setup(b => b.GetNetRevenueAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
            .Returns<DateTime?, DateTime?, CancellationToken>((from, to, ct) =>
            {
                // Current period: 1,200,000, Prior period: 1,000,000 (+20%)
                if (from < DateTime.UtcNow.AddDays(-45)) return Task.FromResult(1000000m);
                return Task.FromResult(1200000m);
            });

        _bookingRepoMock.Setup(b => b.GetActiveGuestsCountAsync(default)).ReturnsAsync(14);
        _bookingRepoMock.Setup(b => b.GetAverageNightlyRateAsync(null, null, default)).ReturnsAsync(45000m);
        _bookingRepoMock.Setup(b => b.GetOccupiedRoomNightsAsync(
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                default))
            .Returns<DateTime, DateTime, CancellationToken>((from, _, _) =>
                Task.FromResult(from < DateTime.UtcNow.Date.AddDays(-45) ? 300 : 360));
        _bookingRepoMock.Setup(b => b.GetDailyRevenueDynamicsAsync(7, default)).ReturnsAsync(new List<RevenuePoint>());
        _bookingRepoMock.Setup(b => b.GetActiveOperationsAsync(5, default)).ReturnsAsync(new List<ActiveOperationDto>());

        _roomRepoMock.Setup(r => r.GetAssetStatusDistributionAsync(default)).ReturnsAsync(new AssetStatusDistribution(12, 8, 0, 0));
        _roomRepoMock.Setup(r => r.GetRoomCountsAsync(default)).ReturnsAsync((20, 12));

        // Act
        var result = await _service.GetOverviewAsync();

        // Assert
        result.Should().NotBeNull();
        result.Kpis.NetRevenue.Should().Be(1500000m);
        result.Kpis.RevenueGrowthPercentage.Should().Be(20.0m);
        result.Kpis.OccupancyRate.Should().Be(60.0); // 360 sold nights / 600 room nights
        result.Kpis.OccupancyGrowthPercentage.Should().Be(20.0); // 60% versus 50%
        result.Kpis.ActiveGuests.Should().Be(14);
        result.Kpis.AvgNightlyRate.Should().Be(45000m);
    }
}
