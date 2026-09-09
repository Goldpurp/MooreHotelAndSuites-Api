using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class Promotion
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DiscountType DiscountType { get; set; }
    public decimal Value { get; set; }
    public decimal? MaximumDiscountAmount { get; set; }
    public string Currency { get; set; } = "NGN";
    public int MinimumNights { get; set; } = 1;
    public Guid? RatePlanId { get; set; }
    public DateTime ValidFromUtc { get; set; }
    public DateTime ValidUntilUtc { get; set; }
    public int? RedemptionLimit { get; set; }
    public int RedemptionCount { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public RatePlan? RatePlan { get; set; }
}
