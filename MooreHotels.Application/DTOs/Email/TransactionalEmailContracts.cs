namespace MooreHotels.Application.DTOs;

public static class TransactionalEmailTemplates
{
    public const string BookingConfirmation = "BookingConfirmation";
    public const string BookingAccessLink = "BookingAccessLink";
    public const string BookingEmailVerification = "BookingEmailVerification";
    public const string AdminNewBooking = "AdminNewBooking";
    public const string Cancellation = "Cancellation";
    public const string PaymentSuccess = "PaymentSuccess";
    public const string CheckOutThankYou = "CheckOutThankYou";
    public const string AdminRefund = "AdminRefund";
    public const string RefundCompleted = "RefundCompleted";
    public const string EmailVerification = "EmailVerification";
    public const string PasswordReset = "PasswordReset";
    public const string StaffWelcome = "StaffWelcome";
    public const string AccountSuspended = "AccountSuspended";
    public const string AccountActivated = "AccountActivated";
    public const string BookingAmendmentConfirmation = "BookingAmendmentConfirmation";
    public const string FolioReceipt = "FolioReceipt";
}

public sealed record BookingAmendmentConfirmationEmail(
    string GuestName,
    string BookingCode,
    string RoomTypeName,
    int RoomQuantity,
    DateTime CheckIn,
    DateTime CheckOut,
    int Nights,
    decimal RoomSubtotal,
    decimal TaxAmount,
    decimal FeeAmount,
    decimal TotalAmount,
    decimal BalanceDue,
    decimal PriceDifference,
    string ManageBookingUrl,
    string AmendmentReason);

public sealed record FolioReceiptEmail(
    string GuestName,
    string BookingCode,
    string ReceiptNumber,
    string EntryType,
    string PaymentMethod,
    decimal Amount,
    string Currency,
    decimal BalanceAfter,
    string Description,
    DateTime ProcessedAtUtc);

public sealed record BookingConfirmationEmail(
    string GuestName,
    string BookingCode,
    string RoomName,
    string RoomCategory,
    int Capacity,
    int AdultCount,
    int ChildCount,
    DateTime CheckIn,
    DateTime CheckOut,
    int Nights,
    decimal TotalAmount,
    string ManageBookingUrl);

public sealed record BookingAccessLinkEmail(
    string GuestName,
    string BookingCode,
    string ManageBookingUrl);

public sealed record BookingEmailVerificationEmail(string VerificationLink);

public sealed record AdminNewBookingEmail(
    string GuestName,
    string BookingCode,
    string RoomName,
    string RoomCategory,
    int Capacity,
    int AdultCount,
    int ChildCount,
    DateTime CheckIn,
    DateTime CheckOut,
    int Nights,
    decimal TotalAmount,
    string GuestEmail,
    string GuestPhone);

public sealed record CancellationEmail(
    string GuestName,
    string BookingCode,
    string RoomName,
    string RoomCategory,
    DateTime CheckIn,
    string? Reason);

public sealed record PaymentSuccessEmail(
    string GuestName,
    string BookingCode,
    string RoomName,
    decimal Amount,
    string Reference);

public sealed record CheckOutThankYouEmail(
    string GuestName,
    string BookingCode,
    string RoomName);

public sealed record AdminRefundEmail(
    string GuestName,
    string BookingCode,
    string RoomName,
    decimal Amount);

public sealed record RefundCompletedEmail(
    string GuestName,
    string BookingCode,
    string RoomName,
    decimal Amount,
    string Reference);

public sealed record EmailVerificationEmail(string Name, string Link);

public sealed record PasswordResetEmail(string Name, string Link);

public sealed record StaffWelcomeEmail(string Name, string SetupLink, string Role);

public sealed record AccountStatusEmail(string Name);
