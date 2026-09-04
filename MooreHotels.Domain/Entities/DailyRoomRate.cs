using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class DailyRoomRate
{
    public Guid Id { get; set; }
    public Guid RatePlanId { get; set; }
    public Guid? RoomId { get; set; }
    public RoomCategory? RoomCategory { get; set; }
    public DateOnly StayDate { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public RatePlan? RatePlan { get; set; }
    public Room? Room { get; set; }
}
