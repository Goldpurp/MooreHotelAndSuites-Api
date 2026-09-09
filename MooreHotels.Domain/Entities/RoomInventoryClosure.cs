namespace MooreHotels.Domain.Entities;

public sealed class RoomInventoryClosure
{
    public Guid Id { get; set; }
    public Guid RoomTypeId { get; set; }
    public Guid? RoomId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int Units { get; set; } = 1;
    public string Reason { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public RoomType? RoomType { get; set; }
    public Room? Room { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
}
