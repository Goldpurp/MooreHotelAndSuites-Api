using MooreHotels.Domain.Entities;

namespace MooreHotels.Domain.Common;

public static class BookingGuestAccessPolicy
{
    public static readonly TimeSpan InitialLinkLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan ReplacementLinkLifetime = TimeSpan.FromHours(2);
    public static readonly TimeSpan ReplacementRequestCooldown = TimeSpan.FromMinutes(1);

    public static bool IsValid(
        Booking booking,
        string? token,
        DateTime utcNow) =>
        booking.GuestAccessTokenIssuedAtUtc.HasValue &&
        booking.GuestAccessTokenExpiresAtUtc > utcNow &&
        booking.GuestAccessTokenRevokedAtUtc is null &&
        BookingGuestAccess.Verify(token, booking.GuestAccessTokenHash);
}
