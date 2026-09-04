using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public record CreateBookingRequest(
    [Required] Guid RoomId,
    [Required, StringLength(80, MinimumLength = 1)] string GuestFirstName,
    [Required, StringLength(80, MinimumLength = 1)] string GuestLastName,
    [Required, EmailAddress, StringLength(254)] string GuestEmail,
    [Required, Phone, StringLength(30, MinimumLength = 7)] string GuestPhone,
    [Required] DateTime CheckIn,
    [Required] DateTime CheckOut,
    [Range(1, 20)] int AdultCount,
    [Range(0, 20)] int ChildCount,
    [Required] PaymentMethod? PaymentMethod,
    [StringLength(1000)] string? Notes,
    [StringLength(128, MinimumLength = 40)] string? EmailVerificationToken = null,
    bool AcceptPrivacyPolicy = false,
    [StringLength(80)] string? PrivacyPolicyVersion = null,
    bool AcceptBookingTerms = false,
    [StringLength(80)] string? BookingTermsVersion = null,
    Guid? QuoteId = null,
    [StringLength(128, MinimumLength = 40)] string? QuoteToken = null);
