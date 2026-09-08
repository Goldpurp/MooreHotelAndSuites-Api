using MooreHotels.Application.DTOs;
using MooreHotels.Application.DTOs.Pricing;

namespace MooreHotels.Application.Interfaces.Services;

public interface IPricingService
{
    Task<PricingQuoteDto> CreateQuoteAsync(
        CreatePricingQuoteRequest request,
        CancellationToken cancellationToken = default);
    Task<PricingQuoteDto> CreateAmendmentQuoteAsync(
        Guid bookingId,
        CreatePricingQuoteRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default);
    Task<ValidatedBookingQuote> ValidateBookingQuoteAsync(
        Guid quoteId,
        string quoteToken,
        CreateBookingRequest booking,
        CancellationToken cancellationToken = default);
    Task<PricingConfigurationDto> GetConfigurationAsync(
        CancellationToken cancellationToken = default);
    Task<RatePlan> SaveRatePlanAsync(
        Guid? id,
        RatePlanRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default);
    Task<DailyRoomRate> SaveDailyRateAsync(
        Guid? id,
        DailyRoomRateRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default);
    Task DeleteDailyRateAsync(
        Guid id,
        Guid actorId,
        CancellationToken cancellationToken = default);
    Task<PricingRule> SaveRuleAsync(
        Guid? id,
        PricingRuleRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default);
    Task<Promotion> SavePromotionAsync(
        Guid? id,
        PromotionRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default);
    Task<int> DeleteExpiredUnconsumedQuotesAsync(
        DateTime utcNow,
        int batchSize = 500,
        CancellationToken cancellationToken = default);
}
