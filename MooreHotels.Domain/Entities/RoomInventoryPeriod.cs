namespace MooreHotels.Domain.Entities;

/// <summary>
/// An effective-dated period during which a physical room contributes one
/// sellable room-night to hotel inventory. EndDate is exclusive.
/// </summary>
public sealed class RoomInventoryPeriod
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public Room? Room { get; set; }
}
