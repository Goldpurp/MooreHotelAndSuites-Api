namespace MooreHotels.Application.Interfaces.Services;

public interface IHotelTimeService
{
    DateOnly Today { get; }
    TimeOnly CheckInTime { get; }
    TimeOnly CheckOutTime { get; }
    DateTime GetCheckInUtc(DateTime calendarDate);
    DateTime GetCheckOutUtc(DateTime calendarDate);
    DateTime ToHotelLocalTime(DateTime utcDateTime);
}
