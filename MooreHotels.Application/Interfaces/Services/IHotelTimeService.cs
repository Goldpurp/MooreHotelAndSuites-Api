namespace MooreHotels.Application.Interfaces.Services;

public interface IHotelTimeService
{
    DateOnly Today { get; }
    TimeOnly CheckInTime { get; }
    TimeOnly CheckOutTime { get; }
    DateTime GetCheckInUtc(DateTime calendarDate);
    DateTime GetCheckOutUtc(DateTime calendarDate);
    DateTime GetLocalDayStartUtc(DateOnly businessDate);
    DateTime GetLocalDayEndUtc(DateOnly businessDate);
    DateTime ToHotelLocalTime(DateTime utcDateTime);
}
