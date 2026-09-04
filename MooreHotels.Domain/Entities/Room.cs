using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public class Room
{
    public Guid Id { get; set; }
    public string RoomNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RoomCategory Category { get; set; }
    public PropertyFloor Floor { get; set; }
    public RoomStatus Status { get; set; }
    public decimal PricePerNight { get; set; }
    public int Capacity { get; set; }
    public string Size { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<string> Amenities { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<RoomImage> Images { get; set; } = new List<RoomImage>();
}