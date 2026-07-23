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
    [Required] PaymentMethod? PaymentMethod,
    [StringLength(1000)] string? Notes);
