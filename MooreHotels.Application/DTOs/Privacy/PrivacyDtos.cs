using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public sealed record PrivacyPolicyDto(
    string PrivacyPolicyVersion,
    string BookingTermsVersion,
    string PrivacyPolicyUrl,
    string BookingTermsUrl,
    bool AcceptanceRequired);

public sealed record CreatePrivacyRequestRequest(
    DataSubjectRequestType Type,
    [StringLength(30)] string? BookingCode,
    [StringLength(2000)] string? Details);

public sealed record UpdatePrivacyRequestRequest(
    DataSubjectRequestStatus Status,
    [StringLength(2000)] string? ResolutionNotes,
    [StringLength(160, MinimumLength = 4)] string? IdentityVerificationReference = null,
    [StringLength(500, MinimumLength = 4)] string? FulfillmentEvidenceReference = null,
    GuestRectificationRequest? Rectification = null,
    bool ConfirmAction = false);

public sealed record GuestRectificationRequest(
    [StringLength(80, MinimumLength = 1)] string? FirstName = null,
    [StringLength(80, MinimumLength = 1)] string? LastName = null,
    [EmailAddress, StringLength(254)] string? Email = null,
    [Phone, StringLength(30)] string? Phone = null);

public sealed record PrivacyRequestDto(
    Guid Id,
    string GuestId,
    DataSubjectRequestType Type,
    DataSubjectRequestStatus Status,
    string? Details,
    DateTime RequestedAtUtc,
    DateTime DueAtUtc,
    DateTime? IdentityVerifiedAtUtc,
    string? IdentityVerificationReference,
    DateTime? FulfilledAtUtc,
    string? FulfillmentEvidenceReference,
    string? FulfillmentDigest,
    DateTime? ExportGeneratedAtUtc,
    DateTime? ResolvedAtUtc,
    string? ResolutionNotes);

public sealed record PlaceLegalHoldRequest(
    [Required, StringLength(500, MinimumLength = 5)] string Reason);

public sealed record ReleaseLegalHoldRequest(
    [Required, StringLength(500, MinimumLength = 5)] string Reason);

public sealed record LegalHoldDto(
    string GuestId,
    bool IsUnderLegalHold,
    DateTime? PlacedAtUtc,
    string? Reason,
    Guid? PlacedByUserId);
