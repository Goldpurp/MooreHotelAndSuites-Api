using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Infrastructure.Hubs;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Services;

public class NotificationService : INotificationService
{
    private readonly MooreHotelsDbContext _db;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        MooreHotelsDbContext db,
        IHubContext<NotificationHub> hubContext,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyNewBookingAsync(Booking booking, string guestName, string roomName)
    {
        var title = "System Alert: New Reservation";
        var message = $"Full Booking Details:\n" +
                      $"Code: {booking.BookingCode}\n" +
                      $"Guest: {guestName}\n" +
                      $"Room: {roomName}\n" +
                      $"Total Amount: {booking.Amount:N2}";

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            Title = title,
            Message = message,
            BookingCode = booking.BookingCode,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        await _db.Notifications.AddAsync(notification);
        await _db.SaveChangesAsync();

        try
        {
            await _hubContext.Clients.Group(StaffConnectionRegistry.StaffGroup).SendAsync(
                "ReceiveNotification",
                new NotificationDto(
                    notification.Id,
                    notification.Title,
                    notification.Message,
                    notification.BookingCode,
                    notification.IsRead,
                    notification.CreatedAt));
        }
        catch (Exception exception)
        {
            // The database notification is the durable source of truth. A
            // disconnected realtime transport must not turn a committed
            // reservation into an apparent booking failure.
            _logger.LogWarning(
                "Realtime delivery failed for durable notification {NotificationId} with {ExceptionType}.",
                notification.Id,
                exception.GetType().Name);
        }
    }

    public async Task<IEnumerable<NotificationDto>> GetUserNotificationsAsync(Guid userId)
    {
        return await _db.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(50)
            .Select(n => new NotificationDto(
                n.Id,
                n.Title,
                n.Message,
                n.BookingCode,
                _db.NotificationReceipts.Any(receipt =>
                    receipt.NotificationId == n.Id && receipt.UserId == userId),
                n.CreatedAt))
            .ToListAsync();
    }

    public async Task<IEnumerable<NotificationDto>> GetStaffNotificationsAsync(Guid userId)
    {
        return await _db.Notifications
            .Where(n => n.UserId == null)
            .OrderByDescending(n => n.CreatedAt)
            .Take(50)
            .Select(n => new NotificationDto(
                n.Id,
                n.Title,
                n.Message,
                n.BookingCode,
                _db.NotificationReceipts.Any(receipt =>
                    receipt.NotificationId == n.Id && receipt.UserId == userId),
                n.CreatedAt))
            .ToListAsync();
    }

    public async Task MarkAsReadAsync(Guid notificationId, Guid userId, bool canManageStaffNotifications)
    {
        var notificationExists = await _db.Notifications.AsNoTracking().AnyAsync(notification =>
            notification.Id == notificationId &&
            (notification.UserId == userId ||
             (canManageStaffNotifications && notification.UserId == null)));
        if (notificationExists)
        {
            var readAtUtc = DateTime.UtcNow;
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO notification_receipts ("NotificationId", "UserId", "ReadAtUtc")
                 VALUES ({notificationId}, {userId}, {readAtUtc})
                 ON CONFLICT ("NotificationId", "UserId") DO NOTHING
                 """);
        }
    }
}
