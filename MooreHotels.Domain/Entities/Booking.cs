using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public class Booking
{
    public Guid Id { get; set; }
    public string BookingCode { get; set; } = string.Empty; // MHS plus six random digits
    public Guid RoomId { get; set; }
    public string GuestId { get; set; } = string.Empty;
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public int AdultCount { get; set; } = 1;
    public int ChildCount { get; set; }
    public BookingStatus Status { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus PaymentStatus { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }
    public string? TransactionReference { get; set; }
    public string? PaymentProviderReference { get; set; }
    public string? PaymentCheckoutUrl { get; set; }
    public DateTime? PaymentCheckoutExpiresAtUtc { get; set; }
    public string? PaymentConfirmationMethod { get; set; }
    public Guid? PaymentConfirmedByUserId { get; set; }
    public DateTime? PaymentConfirmedAtUtc { get; set; }
    public string? RefundReference { get; set; }
    public decimal? RefundAmount { get; set; }
    public string? RefundChannel { get; set; }
    public string? RefundEvidenceType { get; set; }
    public string? RefundNotes { get; set; }
    public Guid? RefundApprovedByUserId { get; set; }
    public DateTime? RefundApprovedAtUtc { get; set; }
    public Guid? RefundProcessedByUserId { get; set; }
    public DateTime? RefundProcessedAtUtc { get; set; }
    public string? Notes { get; set; }
    public string? StatusHistoryJson { get; set; }
    public string? GuestAccessTokenHash { get; set; }
    public DateTime? GuestAccessTokenIssuedAtUtc { get; set; }
    public DateTime? GuestAccessTokenExpiresAtUtc { get; set; }
    public DateTime? GuestAccessTokenRevokedAtUtc { get; set; }
    public DateTime? GuestAccessLinkLastRequestedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? PrivacyPolicyVersion { get; set; }
    public string? BookingTermsVersion { get; set; }
    public DateTime? PoliciesAcceptedAtUtc { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Room? Room { get; set; }
    public Guest? Guest { get; set; }
    public ApplicationUser? PaymentConfirmedByUser { get; set; }
    public ApplicationUser? RefundApprovedByUser { get; set; }
    public ApplicationUser? RefundProcessedByUser { get; set; }
    public ICollection<BookingAddOn> AddOns { get; set; } = new List<BookingAddOn>();
}
