using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using MooreHotels.Domain.Entities;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Identity;

/// <summary>
/// Encrypts every value written through the Identity token store (TOTP
/// authenticator keys, two-factor recovery codes, and any other provider
/// token in AspNetUserTokens) at rest using ASP.NET Data Protection, and
/// decrypts only when a caller reads it back. The default EF Core user
/// store persists these values as plaintext; because a TOTP secret or
/// recovery code is a standing authentication bypass on its own, storing
/// them unencrypted turns any database read exposure (backup leak, replica
/// misconfiguration, over-privileged access) into a silent, total MFA
/// bypass for every enrolled account.
/// </summary>
public sealed class ProtectedUserStore : UserStore<ApplicationUser, IdentityRole<Guid>, MooreHotelsDbContext, Guid>
{
    private const string ProtectorPurpose = "MooreHotels.Identity.UserTokens.v1";

    private readonly IDataProtector _protector;

    public ProtectedUserStore(
        MooreHotelsDbContext context,
        IDataProtectionProvider dataProtectionProvider,
        IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    public override Task SetTokenAsync(
        ApplicationUser user,
        string loginProvider,
        string name,
        string? value,
        CancellationToken cancellationToken = default)
    {
        var protectedValue = value is null ? null : _protector.Protect(value);
        return base.SetTokenAsync(user, loginProvider, name, protectedValue, cancellationToken);
    }

    public override async Task<string?> GetTokenAsync(
        ApplicationUser user,
        string loginProvider,
        string name,
        CancellationToken cancellationToken = default)
    {
        var storedValue = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        if (storedValue is null) return null;
        try
        {
            return _protector.Unprotect(storedValue);
        }
        catch (CryptographicException)
        {
            // A value written before this store was introduced (plaintext),
            // or protected under a since-revoked key, cannot be recovered.
            // Treat it as absent so the caller re-issues a fresh token or
            // enrollment instead of failing outright or leaking ciphertext.
            return null;
        }
    }
}
