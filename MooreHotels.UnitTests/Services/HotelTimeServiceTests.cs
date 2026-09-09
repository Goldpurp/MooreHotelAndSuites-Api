using FluentAssertions;
using Microsoft.Extensions.Options;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Services;
using Xunit;

namespace MooreHotels.UnitTests.Services;

public sealed class HotelTimeServiceTests
{
    private static readonly HotelTimeService Service = new(Options.Create(new HotelSettings
    {
        TimeZoneId = "Africa/Lagos",
        CheckInHour = 14,
        CheckOutHour = 12
    }));

    [Fact]
    public void CheckInAndCheckOut_AreConvertedFromHotelTimeToUtc()
    {
        var date = new DateTime(2026, 9, 4);

        Service.GetCheckInUtc(date).Should().Be(
            new DateTime(2026, 9, 4, 13, 0, 0, DateTimeKind.Utc));
        Service.GetCheckOutUtc(date).Should().Be(
            new DateTime(2026, 9, 4, 11, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ToHotelLocalTime_ConvertsUtcToConfiguredZone()
    {
        var utc = new DateTime(2026, 9, 4, 13, 0, 0, DateTimeKind.Utc);

        Service.ToHotelLocalTime(utc).Should().Be(
            new DateTime(2026, 9, 4, 14, 0, 0, DateTimeKind.Unspecified));
    }
}
