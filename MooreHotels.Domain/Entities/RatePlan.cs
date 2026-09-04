using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class RatePlan
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Currency { get; set; } = "NGN";
    public RateAdjustmentType BaseAdjustmentType { get; set; }
    public decimal BaseAdjustmentValue { get; set; }
    public int MinimumNights { get; set; } = 1;
    public int MaximumNights { get; set; } = 90;
    public DateOnly? SellFromDate { get; set; }
    public DateOnly? SellUntilDate { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<DailyRoomRate> DailyRates { get; set; } = new List<DailyRoomRate>();
}
