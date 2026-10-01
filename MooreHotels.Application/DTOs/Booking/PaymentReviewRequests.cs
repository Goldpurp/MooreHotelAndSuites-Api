using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record ReportTransferRequest([StringLength(128)] string? GuestAccessToken = null);

public sealed record ResolveTransferRequest(
    [Required] string Decision,
    [Required] string ConfirmationText,
    [Required, StringLength(500, MinimumLength = 10)] string Reason,
    [StringLength(120, MinimumLength = 6)] string? BankReference = null,
    [Range(typeof(decimal), "0.01", "99999999999999")] decimal? Amount = null,
    [MaxLength(10)] IReadOnlyList<Guid>? ReplacementRoomIds = null);

public sealed record PaymentReviewItem(
    string BookingCode, string GuestName, string BookingStatus,
    decimal Amount, string Currency, DateTime ReportedAtUtc, bool Overdue, bool RoomHeld);

public sealed record PaymentReviewResult(string BookingCode, string Status, string PaymentStatus, string Message);

public sealed record PaymentReviewRoomOption(Guid RoomId, string RoomNumber, string RoomName);

public sealed record PaymentReviewRoomOptions(
    string BookingCode,
    int RequiredRooms,
    Guid RoomTypeId,
    string RoomTypeName,
    IReadOnlyList<PaymentReviewRoomOption> Rooms);
