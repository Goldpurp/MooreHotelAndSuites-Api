using FluentAssertions;
using Moq;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.Services;
using Xunit;

namespace MooreHotels.UnitTests.Services;

public class AnalyticsServiceTests
{
    private readonly Mock<IBookingRepository> _bookingRepoMock;
    private readonly Mock<IRoomRepository> _roomRepoMock;
    private readonly Mock<IOperationalReportingService> _reportingMock;
    private readonly Mock<IHotelTimeService> _hotelTimeMock;
    private readonly AnalyticsService _service;

    public AnalyticsServiceTests()
    {
        _bookingRepoMock = new Mock<IBookingRepository>();
        _roomRepoMock = new Mock<IRoomRepository>();
        _reportingMock = new Mock<IOperationalReportingService>();
        _hotelTimeMock = new Mock<IHotelTimeService>();
        _service = new AnalyticsService(
            _bookingRepoMock.Object,
            _roomRepoMock.Object,
            _reportingMock.Object,
            _hotelTimeMock.Object);
    }

    [Fact]
    public async Task GetOverviewAsync_CalculatesPositiveRevenueGrowth_Correctly()
    {
        // Arrange
        var today = new DateOnly(2026, 9, 7);
        _hotelTimeMock.SetupGet(time => time.Today).Returns(today);
        _reportingMock.Setup(reporting => reporting.GetReportAsync(
                today.AddDays(-29), today, default))
            .ReturnsAsync(Report(today.AddDays(-29), today, 600, 360, 1200000m, 45000m));
        _reportingMock.Setup(reporting => reporting.GetReportAsync(
                today.AddDays(-59), today.AddDays(-30), default))
            .ReturnsAsync(Report(today.AddDays(-59), today.AddDays(-30), 600, 300, 1000000m, 40000m));

        _bookingRepoMock.Setup(b => b.GetActiveGuestsCountAsync(default)).ReturnsAsync(14);
        _bookingRepoMock.Setup(b => b.GetDailyRevenueDynamicsAsync(7, default)).ReturnsAsync(new List<RevenuePoint>());
        _bookingRepoMock.Setup(b => b.GetActiveOperationsAsync(5, default)).ReturnsAsync(new List<ActiveOperationDto>());

        _roomRepoMock.Setup(r => r.GetAssetStatusDistributionAsync(default)).ReturnsAsync(new AssetStatusDistribution(12, 8, 0, 0));

        // Act
        var result = await _service.GetOverviewAsync();

        // Assert
        result.Should().NotBeNull();
        result.Kpis.NetRevenue.Should().Be(1200000m);
        result.Kpis.RevenueGrowthPercentage.Should().Be(20.0m);
        result.Kpis.OccupancyRate.Should().Be(60.0); // 360 sold nights / 600 room nights
        result.Kpis.OccupancyGrowthPercentage.Should().Be(20.0); // 60% versus 50%
        result.Kpis.ActiveGuests.Should().Be(14);
        result.Kpis.AvgNightlyRate.Should().Be(45000m);
    }

    private static OperationalReportDto Report(
        DateOnly from,
        DateOnly to,
        int available,
        int occupied,
        decimal payments,
        decimal adr) =>
        new(from, to, available, occupied, payments, adr, 0m, payments, 0m,
            0m, 0m, 0, 0, 0, 0, 0);
}
