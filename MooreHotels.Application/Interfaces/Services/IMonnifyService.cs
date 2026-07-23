namespace MooreHotels.Application.Interfaces.Services;

public sealed record MonnifyInitializationResult(
    string CheckoutUrl,
    string PaymentReference,
    string TransactionReference);

public sealed record MonnifyVerificationResult(
    string PaymentReference,
    string? TransactionReference,
    string Status,
    decimal AmountPaid,
    string CurrencyCode,
    string? BookingCode,
    string? CustomerEmail,
    string? CustomerName,
    decimal? Fee,
    decimal? SettlementAmount,
    string? PaymentMethod,
    decimal? TotalPayable = null,
    Guid? BookingId = null,
    DateTime? PaidAtUtc = null);

public interface IMonnifyService
{
    Task<MonnifyInitializationResult> InitializeMonnifyPaymentAsync(
        string email,
        string name,
        decimal amount,
        Guid bookingId,
        string bookingCode,
        string paymentReference,
        string callbackUrl,
        CancellationToken cancellationToken = default);

    Task<MonnifyVerificationResult?> VerifyTransactionAsync(
        string paymentReference,
        CancellationToken cancellationToken = default);

    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
