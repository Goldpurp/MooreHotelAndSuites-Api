namespace MooreHotels.Domain.Entities;

public sealed class NotificationReceipt
{
    public Guid NotificationId { get; set; }
    public Guid UserId { get; set; }
    public DateTime ReadAtUtc { get; set; }

    public Notification? Notification { get; set; }
    public ApplicationUser? User { get; set; }
}
