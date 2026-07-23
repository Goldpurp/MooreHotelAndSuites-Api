using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Interfaces.Services;

public interface INotificationService
{
    Task NotifyNewBookingAsync(Booking booking, string guestName, string roomName);
    Task<IEnumerable<NotificationDto>> GetUserNotificationsAsync(Guid userId);
    Task<IEnumerable<NotificationDto>> GetStaffNotificationsAsync();
    Task MarkAsReadAsync(Guid notificationId, Guid userId, bool canManageStaffNotifications);
}
