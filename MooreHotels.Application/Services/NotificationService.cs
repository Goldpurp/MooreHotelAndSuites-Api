using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Services;

public class NotificationService : INotificationService
{
    // The actual broadcasting logic is implemented in MooreHotels.Infrastructure
    // to keep the Application project clean of ASP.NET Core framework dependencies.
    
    public NotificationService() { }

    public Task NotifyNewBookingAsync(Booking booking, string guestName, string roomName)
    {
        return Task.CompletedTask;
    }

    public Task<IEnumerable<NotificationDto>> GetUserNotificationsAsync(Guid userId)
    {
        return Task.FromResult(Enumerable.Empty<NotificationDto>());
    }

    public Task<IEnumerable<NotificationDto>> GetStaffNotificationsAsync()
    {
        return Task.FromResult(Enumerable.Empty<NotificationDto>());
    }

    public Task MarkAsReadAsync(Guid notificationId, Guid userId, bool canManageStaffNotifications)
    {
        return Task.CompletedTask;
    }
}
