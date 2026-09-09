using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class RoomType
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RoomCategory Category { get; set; }
    public int BaseOccupancy { get; set; } = 1;
    public int MaxOccupancy { get; set; } = 2;
    public decimal BasePricePerNight { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<string> Amenities { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<Room> Rooms { get; set; } = new List<Room>();
    public ICollection<ReservationRoom> ReservationRooms { get; set; } = new List<ReservationRoom>();
}
