using System.Collections.Concurrent;
using MooreHotels.Application.Interfaces;

namespace MooreHotels.IntegrationTests;

public sealed record RecordedEmail(
    string Template,
    string Recipient,
    string? BookingCode = null);

public sealed class RecordingEmailService : IEmailService
{
    private readonly ConcurrentQueue<RecordedEmail> _messages = new();

    public IReadOnlyList<RecordedEmail> Messages => _messages.ToArray();

    public void Reset() => _messages.Clear();

    private Task Record(
        string template,
        string recipient,
        string? bookingCode = null)
    {
        _messages.Enqueue(new RecordedEmail(
            template,
            recipient,
            bookingCode));
        return Task.CompletedTask;
    }

    public Task SendBookingConfirmationAsync(
        string email,
        string guestName,
        string bookingCode,
        string roomName,
        string roomCategory,
        int capacity,
        DateTime checkIn,
        DateTime checkOut,
        int nights,
        decimal totalAmount) =>
        Record("BookingConfirmation", email, bookingCode);

    public Task SendCancellationNoticeAsync(
        string email,
        string guestName,
        string bookingCode,
        string roomName,
        string roomCategory,
        DateTime checkIn,
        string? reason = null) =>
        Record("Cancellation", email, bookingCode);

    public Task SendCheckInReminderAsync(
        string email,
        string guestName,
        string bookingCode,
        string roomName,
        DateTime checkIn) =>
        Record("CheckInReminder", email, bookingCode);

    public Task SendEmailVerificationAsync(
        string email,
        string name,
        string link) =>
        Record("EmailVerification", email);

    public Task SendPasswordResetAsync(
        string email,
        string name,
        string link) =>
        Record("PasswordReset", email);

    public Task SendPaymentSuccessAsync(
        string email,
        string guestName,
        string bookingCode,
        string roomName,
        decimal amount,
        string reference) =>
        Record("PaymentSuccess", email, bookingCode);

    public Task SendCheckOutThankYouAsync(
        string email,
        string guestName,
        string bookingCode,
        string roomName) =>
        Record("CheckOutThankYou", email, bookingCode);

    public Task SendAdminNewBookingAlertAsync(
        string adminEmail,
        string guestName,
        string bookingCode,
        string roomName,
        string roomCategory,
        int capacity,
        DateTime checkIn,
        DateTime checkOut,
        int nights,
        decimal totalAmount,
        string guestEmail,
        string guestPhone) =>
        Record("AdminNewBooking", adminEmail, bookingCode);

    public Task SendStaffWelcomeEmailAsync(
        string email,
        string name,
        string setupLink,
        string role) =>
        Record("StaffWelcome", email);

    public Task SendAccountSuspendedAsync(
        string email,
        string name) =>
        Record("AccountSuspended", email);

    public Task SendAccountActivatedAsync(
        string email,
        string name) =>
        Record("AccountActivated", email);

    public Task SendRefundCompletionNoticeAsync(
        string email,
        string guestName,
        string bookingCode,
        string roomName,
        decimal amount,
        string reference) =>
        Record("RefundCompleted", email, bookingCode);

    public Task SendAdminRefundAlertAsync(
        string adminEmail,
        string guestName,
        string bookingCode,
        string roomName,
        decimal amount) =>
        Record("AdminRefund", adminEmail, bookingCode);
}
