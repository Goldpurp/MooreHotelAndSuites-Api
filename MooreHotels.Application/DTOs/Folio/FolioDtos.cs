using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public sealed record FolioEntryDto(
    Guid Id,
    FolioEntryType Type,
    FolioEntryDirection Direction,
    decimal Amount,
    string Currency,
    string Description,
    string SourceType,
    string? SourceId,
    string? ExternalReference,
    Guid? ReversesEntryId,
    DateTime PostedAtUtc,
    Guid? PostedByUserId,
    string? Notes);

public sealed record FolioSummaryDto(
    Guid Id,
    string Currency,
    FolioStatus Status,
    decimal TotalDebits,
    decimal TotalCredits,
    decimal Balance,
    decimal AmountDue,
    decimal GuestCredit,
    decimal Payments,
    decimal Refunds,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc);

public sealed record FolioDto(
    Guid Id,
    Guid BookingId,
    string BookingCode,
    string Currency,
    FolioStatus Status,
    decimal TotalDebits,
    decimal TotalCredits,
    decimal Balance,
    decimal AmountDue,
    decimal GuestCredit,
    decimal Payments,
    decimal Refunds,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    IReadOnlyList<FolioEntryDto> Entries);

public sealed record PostFolioChargeRequest(
    FolioEntryType Type,
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal Amount,
    [Required, StringLength(200)] string Description,
    [Required, StringLength(80)] string SourceType,
    [StringLength(160)] string? SourceId,
    [Required, StringLength(100, MinimumLength = 8)] string IdempotencyKey,
    [StringLength(500)] string? Notes = null);

public sealed record PostFolioPaymentRequest(
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal Amount,
    [Required, RegularExpression("^(BankTransfer|Cash|Monnify|Other)$")] string Method,
    [Required, StringLength(160, MinimumLength = 4)] string ExternalReference,
    [Required, StringLength(100, MinimumLength = 8)] string IdempotencyKey,
    bool ConfirmReservation = true,
    [StringLength(500)] string? Notes = null);

public sealed record PostFolioCreditRequest(
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal Amount,
    [Required, StringLength(200, MinimumLength = 4)] string Reason,
    [Required, StringLength(100, MinimumLength = 8)] string IdempotencyKey,
    [StringLength(500)] string? Notes = null);

public sealed record VoidFolioEntryRequest(
    [Required, StringLength(500, MinimumLength = 10)] string Reason,
    [Required, StringLength(100, MinimumLength = 8)] string IdempotencyKey);
