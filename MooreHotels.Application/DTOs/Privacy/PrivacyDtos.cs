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
    [StringLength(2000)] string? ResolutionNotes);

public sealed record PrivacyRequestDto(
    Guid Id,
    string GuestId,
    DataSubjectRequestType Type,
    DataSubjectRequestStatus Status,
    string? Details,
    DateTime RequestedAtUtc,
    DateTime? ResolvedAtUtc,
    string? ResolutionNotes);
