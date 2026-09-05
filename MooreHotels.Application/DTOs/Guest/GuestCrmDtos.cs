using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record GuestPreferencesDto(
    string? PreferredLanguage,
    string? BeddingPreference,
    string? DietaryNotes,
    string? AccessibilityNeeds,
    bool MarketingOptIn);

public sealed record GuestStayHistoryDto(
    Guid BookingId,
    string BookingCode,
    DateTime CheckIn,
    DateTime CheckOut,
    string RoomTypeName,
    int RoomQuantity,
    string Status,
    decimal TotalAmount);

public sealed record GuestCrmProfileDto(
    string Id,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    DateTime? EmailVerifiedAtUtc,
    DateTime? PhoneVerifiedAtUtc,
    GuestPreferencesDto Preferences,
    IReadOnlyList<GuestStayHistoryDto> StayHistory,
    IReadOnlyList<GuestNoteDto> Notes);

public sealed record GuestNoteDto(
    Guid Id,
    string Body,
    bool IsSensitive,
    DateTime CreatedAtUtc,
    Guid CreatedByUserId);

public sealed record UpdateGuestPreferencesRequest(
    [StringLength(40)] string? PreferredLanguage,
    [StringLength(80)] string? BeddingPreference,
    [StringLength(300)] string? DietaryNotes,
    [StringLength(300)] string? AccessibilityNeeds,
    bool MarketingOptIn);

public sealed record AddGuestNoteRequest(
    [Required, StringLength(1000, MinimumLength = 4)] string Body,
    bool IsSensitive = true);

public sealed record VerifyGuestContactRequest(
    [Required, RegularExpression("^(Email|Phone)$")] string ContactType,
    [Required, StringLength(160, MinimumLength = 4)] string EvidenceReference);

public sealed record GuestDuplicateCandidateDto(
    string GuestId,
    string GuestName,
    string Email,
    string Phone,
    int BookingCount,
    IReadOnlyList<string> MatchReasons);

public sealed record MergeGuestRequest(
    [Required, StringLength(20)] string PrimaryGuestId,
    [Required, StringLength(20)] string DuplicateGuestId,
    [Required, RegularExpression("^(VerifiedEmail|VerifiedPhone|GovernmentIdReviewed|ManualReview)$")]
    string EvidenceType,
    [Required, StringLength(500, MinimumLength = 10)] string Reason);

public sealed record GuestMergeDto(
    Guid Id,
    string PrimaryGuestId,
    string DuplicateGuestId,
    int MovedBookingCount,
    string EvidenceType,
    string Reason,
    DateTime MergedAtUtc,
    Guid MergedByUserId);
