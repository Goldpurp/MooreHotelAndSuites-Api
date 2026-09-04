namespace MooreHotels.Domain.Common;

public sealed class PricingSettings
{
    public string DefaultCurrency { get; init; } = "NGN";
    public int QuoteLifetimeMinutes { get; init; } = 15;
    public bool RequireQuoteForBooking { get; init; }
}
