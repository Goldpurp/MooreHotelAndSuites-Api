namespace MooreHotels.Domain.Common;

public static class BookingEmailVerificationPolicy
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RequestCooldown = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RetentionAfterExpiry = TimeSpan.FromDays(1);
}

public sealed record BookingEmailVerificationProof(
    string Email,
    string TokenHash,
    DateTime VerifiedAtUtc);
