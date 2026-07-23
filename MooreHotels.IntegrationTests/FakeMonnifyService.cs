using MooreHotels.Application.Interfaces.Services;
using System.Collections.Concurrent;

namespace MooreHotels.IntegrationTests;

public sealed class FakeMonnifyService : IMonnifyService
{
    private readonly ConcurrentDictionary<string, MonnifyVerificationResult>
        _verificationResults = new(StringComparer.Ordinal);
    private int _verificationCalls;
    private int _initializationCalls;

    public int VerificationCalls => Volatile.Read(ref _verificationCalls);
    public int InitializationCalls => Volatile.Read(ref _initializationCalls);

    public void SetVerification(
        string paymentReference,
        MonnifyVerificationResult? result)
    {
        if (result is null)
        {
            _verificationResults.TryRemove(paymentReference, out _);
        }
        else
        {
            _verificationResults[paymentReference] = result;
        }
    }

    public void Reset()
    {
        _verificationResults.Clear();
        Interlocked.Exchange(ref _verificationCalls, 0);
        Interlocked.Exchange(ref _initializationCalls, 0);
    }

    public Task<MonnifyInitializationResult> InitializeMonnifyPaymentAsync(
        string email,
        string name,
        decimal amount,
        Guid bookingId,
        string bookingCode,
        string paymentReference,
        string callbackUrl,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _initializationCalls);
        return Task.FromResult(new MonnifyInitializationResult(
            $"https://sandbox.sdk.monnify.com/checkout/{Uri.EscapeDataString(bookingCode)}",
            paymentReference,
            $"MNFY|TEST|{Guid.NewGuid():N}"));
    }

    public Task<MonnifyVerificationResult?> VerifyTransactionAsync(
        string paymentReference,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _verificationCalls);
        _verificationResults.TryGetValue(paymentReference, out var result);
        return Task.FromResult(result);
    }

    public Task<string> GetAccessTokenAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult("integration-test-token");
}
