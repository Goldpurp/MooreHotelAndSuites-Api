using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class PricingRule
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PricingRuleKind Kind { get; set; }
    public PricingRuleCalculation Calculation { get; set; }
    public decimal Value { get; set; }
    public string Currency { get; set; } = "NGN";
    public bool IsInclusive { get; set; }
    public DateOnly? EffectiveFromDate { get; set; }
    public DateOnly? EffectiveUntilDate { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
