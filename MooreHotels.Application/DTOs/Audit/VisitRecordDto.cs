
namespace MooreHotels.Application.DTOs;

public record VisitRecordDto(
    Guid Id,
    string GuestId,
    string GuestName,
    string RoomNumber,
    string BookingCode,
    string Action,
    DateTime Timestamp,
    string AuthorizedBy,
    string RoomName = "",
    Guid RoomId = default);
