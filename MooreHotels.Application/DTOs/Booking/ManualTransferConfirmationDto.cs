namespace MooreHotels.Application.DTOs;

public static class ManualTransferConfirmation
{
    public const string RequiredText = "ACCEPT";
    public const string Method = "TypedAcknowledgement";
}

public sealed record ManualTransferConfirmationDto(
    string BookingCode,
    string PaymentStatus,
    string Status,
    string TransactionReference,
    string ConfirmationMethod,
    DateTime ConfirmedAtUtc);

public sealed record ManualTransferConfirmationResponse(
    string Message,
    ManualTransferConfirmationDto Data);
