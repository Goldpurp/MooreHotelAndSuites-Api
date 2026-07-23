using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public enum MonnifyPaymentOutcomeKind
{
    Confirmed,
    AlreadyProcessed,
    PaidAfterExpiry
}

public sealed record MonnifyPaymentOutcome(
    MonnifyPaymentOutcomeKind Kind,
    string BookingCode,
    string PaymentStatus,
    string BookingStatus,
    string PaymentReference,
    string ProviderTransactionReference,
    DateTime ProcessedAtUtc);

public interface IMonnifyPaymentProcessor
{
    Task<MonnifyPaymentOutcome> ApplyVerifiedPaymentAsync(
        Guid bookingId,
        MonnifyVerificationResult verification,
        string source,
        Guid? actingUserId,
        string requestId,
        CancellationToken cancellationToken = default);
}
