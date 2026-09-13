using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Interfaces.Services;

public interface IFolioService
{
    Task<FolioDto> GetByBookingCodeAsync(string bookingCode, CancellationToken cancellationToken = default);
    Task<FolioDto> PostChargeAsync(string bookingCode, PostFolioChargeRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<FolioDto> PostPaymentAsync(string bookingCode, PostFolioPaymentRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<FolioDto> PostCreditAsync(string bookingCode, PostFolioCreditRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<FolioDto> VoidEntryAsync(string bookingCode, Guid entryId, VoidFolioEntryRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<FolioDto> CloseAsync(string bookingCode, Guid actorId, CancellationToken cancellationToken = default);
    Task ApplyCancellationCreditAsync(Booking booking, string reason, Guid actorId, CancellationToken cancellationToken = default);
    Task ApplyNoShowPolicyAsync(Booking booking, string reason, Guid actorId, CancellationToken cancellationToken = default);
    Task<Booking> ApplyRefundAsync(Guid bookingId, decimal amount, string reference, string channel, string? notes, Guid actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and posts a staff-completed refund against a booking that is
    /// awaiting a manual refund, including the dual-approval requirement above
    /// <paramref name="highValueThreshold"/>. Every state-dependent check
    /// (guest credit balance, prior approval, idempotent replay) is
    /// re-evaluated under the same booking-row lock that posts the folio
    /// entry, so two concurrent completions for the same booking cannot both
    /// act on a pre-lock snapshot of the guest credit or approval fields.
    /// </summary>
    Task<RefundCompletionResult> CompleteApprovedRefundAsync(
        Guid bookingId,
        decimal amount,
        string reference,
        string channel,
        string evidenceType,
        string? notes,
        Guid processingUserId,
        decimal highValueThreshold,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the booking row (SELECT ... FOR UPDATE) and its folio for the
    /// duration of <paramref name="mutation"/>, then saves in the same
    /// transaction. A caller-owned transaction remains the caller's to commit
    /// or roll back; otherwise this method creates and commits one. The
    /// change tracker is cleared first, so callers must save pending changes
    /// before invoking this method. The mutation
    /// always sees the current database state, never a stale pre-lock copy
    /// held by an earlier, unlocked read in the same request. Use this for
    /// any booking/folio state change gated on a read-then-decide check
    /// (balances, approval fields) that must not race with a concurrent
    /// request against the same booking.
    /// </summary>
    Task<Booking> ExecuteUnderBookingLockAsync(
        Guid bookingId,
        Func<Booking, Folio, Task> mutation,
        CancellationToken cancellationToken = default);
}

/// <param name="Booking">The booking after the call, saved in the current transaction.</param>
/// <param name="Applied">
/// False when the call recognized an idempotent replay (matching reference
/// and amount already posted) and made no new change; true when a new
/// refund entry and booking update were just committed.
/// </param>
public sealed record RefundCompletionResult(Booking Booking, bool Applied);
