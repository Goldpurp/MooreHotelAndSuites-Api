namespace MooreHotels.Application.Interfaces;

public interface IEmailService
{
    // Guest Communications
    Task SendBookingConfirmationAsync(string email, string guestName, string bookingCode, string roomName, string roomCategory, int capacity, DateTime checkIn, DateTime checkOut, int nights, decimal totalAmount);
    Task SendCancellationNoticeAsync(string email, string guestName, string bookingCode, string roomName, string roomCategory, DateTime checkIn, string? reason = null);
    Task SendCheckInReminderAsync(string email, string guestName, string bookingCode, string roomName, DateTime checkIn);
    Task SendEmailVerificationAsync(string email, string name, string link);
    Task SendPasswordResetAsync(string email, string name, string link);
    Task SendPaymentSuccessAsync(string email, string guestName, string bookingCode, string roomName, decimal amount, string reference);
    Task SendCheckOutThankYouAsync(string email, string guestName, string bookingCode, string roomName);
    Task SendAdminNewBookingAlertAsync(string adminEmail, string guestName, string bookingCode, string roomName, string roomCategory, int capacity, DateTime checkIn, DateTime checkOut, int nights, decimal totalAmount, string guestEmail, string guestPhone);
    Task SendStaffWelcomeEmailAsync(string email, string name, string setupLink, string role);
    Task SendAccountSuspendedAsync(string email, string name);
    Task SendAccountActivatedAsync(string email, string name);
    Task SendRefundCompletionNoticeAsync(string email, string guestName, string bookingCode, string roomName, decimal amount, string reference);
    Task SendAdminRefundAlertAsync(string adminEmail, string guestName, string bookingCode, string roomName, decimal amount);
}
