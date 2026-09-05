namespace MooreHotels.Domain.Entities;

public sealed class BookingQuote
{
    public Guid Id { get; set; }
    public string AccessTokenHash { get; set; } = string.Empty;
    public Guid? RoomId { get; set; }
    public Guid RoomTypeId { get; set; }
    public int RoomQuantity { get; set; } = 1;
    public Guid RatePlanId { get; set; }
    public Guid? PromotionId { get; set; }
    public DateOnly CheckInDate { get; set; }
    public DateOnly CheckOutDate { get; set; }
    public int AdultCount { get; set; }
    public int ChildCount { get; set; }
    public string Currency { get; set; } = "NGN";
    public decimal RoomSubtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal IncludedTaxAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal FeeAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public Room? Room { get; set; }
    public RoomType? RoomType { get; set; }
    public RatePlan? RatePlan { get; set; }
    public Promotion? Promotion { get; set; }
    public Booking? Booking { get; set; }
    public ICollection<BookingQuoteLine> Lines { get; set; } = new List<BookingQuoteLine>();
}
