namespace MooreHotels.Domain.Entities;

public class BookingAddOn
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public Guid AddOnServiceId { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public string? Notes { get; set; }
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;

    public Booking? Booking { get; set; }
    public AddOnService? AddOnService { get; set; }
}
