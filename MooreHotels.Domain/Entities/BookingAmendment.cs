namespace MooreHotels.Domain.Entities;

public sealed class BookingAmendment
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string PreviousStateJson { get; set; } = "{}";
    public string NewStateJson { get; set; } = "{}";
    public decimal PreviousAmount { get; set; }
    public decimal NewAmount { get; set; }
    public decimal PriceDifference { get; set; }
    public Guid RoomTypeId { get; set; }
    public int RoomQuantity { get; set; }
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public int AdultCount { get; set; }
    public int ChildCount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime AmendedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid AmendedByUserId { get; set; }
    public Booking? Booking { get; set; }
    public ApplicationUser? AmendedByUser { get; set; }
}
