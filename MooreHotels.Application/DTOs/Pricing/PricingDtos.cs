using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs.Pricing;

public sealed record CreatePricingQuoteRequest(
    Guid? RoomId,
    [Required] DateTime CheckIn,
    [Required] DateTime CheckOut,
    [Range(1, 20)] int AdultCount,
    [Range(0, 20)] int ChildCount,
    [StringLength(30)] string? RatePlanCode = null,
    [StringLength(40)] string? PromotionCode = null,
    Guid? RoomTypeId = null,
    [Range(1, 10)] int RoomQuantity = 1);

public sealed record PricingQuoteLineDto(
    PricingLineType Type,
    string Code,
    string Description,
    DateOnly? StayDate,
    int Quantity,
    decimal UnitAmount,
    decimal Amount,
    bool IsInclusive);

public sealed record PricingQuoteDto(
    Guid QuoteId,
    string QuoteToken,
    Guid? RoomId,
    Guid RoomTypeId,
    string RoomTypeCode,
    string RoomTypeName,
    int RoomQuantity,
    string RatePlanCode,
    string RatePlanName,
    string? PromotionCode,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    int AdultCount,
    int ChildCount,
    int Nights,
    string Currency,
    decimal RoomSubtotal,
    decimal DiscountAmount,
    decimal IncludedTaxAmount,
    decimal TaxAmount,
    decimal FeeAmount,
    decimal TotalAmount,
    DateTime ExpiresAtUtc,
    IReadOnlyList<PricingQuoteLineDto> Lines);

public sealed record RatePlanRequest(
    [Required, StringLength(30)] string Code,
    [Required, StringLength(120)] string Name,
    [StringLength(1000)] string? Description,
    [Required, StringLength(3, MinimumLength = 3)] string Currency,
    RateAdjustmentType BaseAdjustmentType,
    decimal BaseAdjustmentValue,
    [Range(1, 90)] int MinimumNights,
    [Range(1, 90)] int MaximumNights,
    DateOnly? SellFromDate,
    DateOnly? SellUntilDate,
    bool IsDefault,
    bool IsActive);

public sealed record DailyRoomRateRequest(
    [Required] Guid RatePlanId,
    Guid? RoomId,
    RoomCategory? RoomCategory,
    [Required] DateOnly StayDate,
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal Amount,
    Guid? RoomTypeId = null);

public sealed record PricingRuleRequest(
    [Required, StringLength(30)] string Code,
    [Required, StringLength(120)] string Name,
    PricingRuleKind Kind,
    PricingRuleCalculation Calculation,
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal Value,
    [Required, StringLength(3, MinimumLength = 3)] string Currency,
    bool IsInclusive,
    DateOnly? EffectiveFromDate,
    DateOnly? EffectiveUntilDate,
    int SortOrder,
    bool IsActive);

public sealed record PromotionRequest(
    [Required, StringLength(40)] string Code,
    [Required, StringLength(120)] string Name,
    DiscountType DiscountType,
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal Value,
    decimal? MaximumDiscountAmount,
    [Required, StringLength(3, MinimumLength = 3)] string Currency,
    [Range(1, 90)] int MinimumNights,
    Guid? RatePlanId,
    DateTime ValidFromUtc,
    DateTime ValidUntilUtc,
    int? RedemptionLimit,
    bool IsActive);

public sealed record PricingConfigurationDto(
    IReadOnlyList<RatePlan> RatePlans,
    IReadOnlyList<DailyRoomRate> DailyRates,
    IReadOnlyList<PricingRule> Rules,
    IReadOnlyList<Promotion> Promotions);

public sealed record RatePlan(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    string Currency,
    RateAdjustmentType BaseAdjustmentType,
    decimal BaseAdjustmentValue,
    int MinimumNights,
    int MaximumNights,
    DateOnly? SellFromDate,
    DateOnly? SellUntilDate,
    bool IsDefault,
    bool IsActive,
    DateTime UpdatedAtUtc);

public sealed record DailyRoomRate(
    Guid Id,
    Guid RatePlanId,
    Guid? RoomId,
    Guid? RoomTypeId,
    RoomCategory? RoomCategory,
    DateOnly StayDate,
    decimal Amount,
    DateTime UpdatedAtUtc);

public sealed record PricingRule(
    Guid Id,
    string Code,
    string Name,
    PricingRuleKind Kind,
    PricingRuleCalculation Calculation,
    decimal Value,
    string Currency,
    bool IsInclusive,
    DateOnly? EffectiveFromDate,
    DateOnly? EffectiveUntilDate,
    int SortOrder,
    bool IsActive,
    DateTime UpdatedAtUtc);

public sealed record Promotion(
    Guid Id,
    string Code,
    string Name,
    DiscountType DiscountType,
    decimal Value,
    decimal? MaximumDiscountAmount,
    string Currency,
    int MinimumNights,
    Guid? RatePlanId,
    DateTime ValidFromUtc,
    DateTime ValidUntilUtc,
    int? RedemptionLimit,
    int RedemptionCount,
    bool IsActive,
    DateTime UpdatedAtUtc);

public sealed record ValidatedBookingQuote(
    Guid QuoteId,
    string AccessTokenHash,
    Guid? RoomId,
    Guid RoomTypeId,
    int RoomQuantity,
    string Currency,
    decimal RoomSubtotal,
    decimal DiscountAmount,
    decimal IncludedTaxAmount,
    decimal TaxAmount,
    decimal FeeAmount,
    decimal TotalAmount,
    DateTime ValidatedAtUtc);
