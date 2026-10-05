using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class ReportingPeriodTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData("day", 1)]
    [InlineData("week", 7)]
    [InlineData("month", 30)]
    public async Task Selected_period_controls_totals_chart_and_room_nights(string period, int days)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var analytics = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var result = await analytics.GetOverviewAsync(period);
        Assert.Equal(time.Today.AddDays(1 - days), result.FromDate);
        Assert.Equal(time.Today, result.ToDate);
        Assert.Equal(days, result.RevenueDynamics.Count);
        Assert.Equal(result.Kpis.NetRevenue, result.RevenueDynamics.Sum(point => point.Value));
        Assert.NotNull(result.Report);
        Assert.Equal(result.Report.Payments - result.Report.Refunds, result.Kpis.NetRevenue);
        Assert.Equal(result.Report.Adr, result.Kpis.AvgNightlyRate);
        Assert.Equal(result.FromDate.ToString("yyyy-MM-dd"), result.RevenueDynamics[0].Date);
    }

    [Fact]
    public async Task Hotel_midnight_boundary_uses_ledger_posting_date_and_excludes_unpaid_bookings()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var analytics = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var before = await analytics.GetOverviewAsync("day");
        var start = time.GetLocalDayStartUtc(time.Today);
        var included = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed, paymentConfirmedAtUtc: start.AddMinutes(30));
        await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed, paymentConfirmedAtUtc: start.AddTicks(-1));
        await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed, paymentConfirmedAtUtc: time.GetLocalDayEndUtc(time.Today));
        await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Unpaid);
        var after = await analytics.GetOverviewAsync("day");
        Assert.Equal(included.Amount, after.Kpis.NetRevenue - before.Kpis.NetRevenue);
        Assert.Equal(after.Kpis.NetRevenue, Assert.Single(after.RevenueDynamics).Value);
    }
}
