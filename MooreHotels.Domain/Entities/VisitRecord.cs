
namespace MooreHotels.Domain.Entities;

public class VisitRecord
{
    public Guid Id { get; set; }
    public string GuestId { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public Guid RoomId { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public string RoomNumber { get; set; } = string.Empty;
    public string BookingCode { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string AuthorizedBy { get; set; } = string.Empty;
}
