using Microsoft.Extensions.Options;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.Application.Services;

public sealed class HotelTimeService : IHotelTimeService
{
    private readonly TimeZoneInfo _timeZone;

    public HotelTimeService(IOptions<HotelSettings> settings)
    {
        var value = settings.Value;
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(value.TimeZoneId);
        CheckInTime = new TimeOnly(value.CheckInHour, 0);
        CheckOutTime = new TimeOnly(value.CheckOutHour, 0);
    }

    public DateOnly Today => DateOnly.FromDateTime(ToHotelLocalTime(DateTime.UtcNow));

    public TimeOnly CheckInTime { get; }

    public TimeOnly CheckOutTime { get; }

    public DateTime GetCheckInUtc(DateTime calendarDate) =>
        ToUtc(calendarDate, CheckInTime);

    public DateTime GetCheckOutUtc(DateTime calendarDate) =>
        ToUtc(calendarDate, CheckOutTime);

    public DateTime GetLocalDayStartUtc(DateOnly businessDate) =>
        ToUtc(businessDate.ToDateTime(TimeOnly.MinValue), TimeOnly.MinValue);

    public DateTime GetLocalDayEndUtc(DateOnly businessDate) =>
        GetLocalDayStartUtc(businessDate.AddDays(1));

    public DateTime ToHotelLocalTime(DateTime utcDateTime)
    {
        var utc = utcDateTime.Kind == DateTimeKind.Utc
            ? utcDateTime
            : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, _timeZone);
    }

    private DateTime ToUtc(DateTime calendarDate, TimeOnly localTime)
    {
        var localDate = DateOnly.FromDateTime(calendarDate);
        var unspecifiedLocal = DateTime.SpecifyKind(
            localDate.ToDateTime(localTime),
            DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecifiedLocal, _timeZone);
    }
}
