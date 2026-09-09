namespace MooreHotels.Domain.Entities;

public sealed class ReservationRoom
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public Guid RoomTypeId { get; set; }
    public string RoomTypeCode { get; set; } = string.Empty;
    public string RoomTypeName { get; set; } = string.Empty;
    public Guid? AssignedRoomId { get; set; }
    public int Sequence { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AssignedAtUtc { get; set; }
    public Guid? AssignedByUserId { get; set; }
    public Booking? Booking { get; set; }
    public RoomType? RoomType { get; set; }
    public Room? AssignedRoom { get; set; }
    public ApplicationUser? AssignedByUser { get; set; }
}
