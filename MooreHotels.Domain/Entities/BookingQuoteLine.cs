using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class BookingQuoteLine
{
    public Guid Id { get; set; }
    public Guid BookingQuoteId { get; set; }
    public PricingLineType Type { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateOnly? StayDate { get; set; }
    public int Quantity { get; set; }
    public decimal UnitAmount { get; set; }
    public decimal Amount { get; set; }
    public bool IsInclusive { get; set; }
    public int SortOrder { get; set; }
    public BookingQuote? BookingQuote { get; set; }
}
