namespace MooreHotels.Application.Interfaces.Repositories;

public sealed record ExpiredBookingNotification(
    string GuestEmail,
    string GuestName,
    string BookingCode,
    string RoomName,
    string RoomCategory,
    DateTime CheckIn);
