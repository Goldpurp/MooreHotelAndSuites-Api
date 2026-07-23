using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Common;

public static class BookingPaymentPolicy
{
    public static readonly TimeSpan ConfirmationWindow = TimeSpan.FromHours(1);
    public static readonly Guid SystemActorId = Guid.Empty;

    public static DateTime GetConfirmationDeadlineUtc(DateTime createdAtUtc) =>
        createdAtUtc.Add(ConfirmationWindow);

    public static DateTime GetExpirationCutoffUtc(DateTime utcNow) =>
        utcNow.Subtract(ConfirmationWindow);

    public static bool IsExpiredUnconfirmed(
        BookingStatus bookingStatus,
        PaymentStatus paymentStatus,
        DateTime createdAtUtc,
        DateTime utcNow) =>
        bookingStatus == BookingStatus.Pending &&
        paymentStatus is PaymentStatus.Unpaid or PaymentStatus.AwaitingVerification &&
        createdAtUtc <= GetExpirationCutoffUtc(utcNow);
}
