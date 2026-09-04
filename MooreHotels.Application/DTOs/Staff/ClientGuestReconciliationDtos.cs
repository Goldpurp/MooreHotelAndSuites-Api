using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record GuestLinkCandidateDto(
    string GuestId,
    string Name,
    string Email,
    string Phone);

public sealed record ClientGuestLinkIssueDto(
    Guid UserId,
    string Name,
    string Email,
    string? CurrentGuestId,
    string Issue,
    IReadOnlyList<GuestLinkCandidateDto> EmailMatches);

public sealed record ReconcileClientGuestRequest(
    [Required, StringLength(20, MinimumLength = 4)] string GuestId,
    [Required, RegularExpression("^(VerifiedEmail|VerifiedPhone|GovernmentId|InPersonIdentityCheck)$")]
    string EvidenceType,
    [Required, StringLength(500, MinimumLength = 10)] string Reason);

public sealed record ClientGuestReconciliationResult(
    Guid UserId,
    string GuestId,
    string EvidenceType,
    DateTime ReconciledAtUtc);
