using Microsoft.AspNetCore.DataProtection;
using MooreHotels.Application.Interfaces;

namespace MooreHotels.Infrastructure.Services;

public sealed class BookingGuestAccessProtector : IBookingGuestAccessProtector
{
    private readonly IDataProtector _protector;

    public BookingGuestAccessProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector(
            "MooreHotels.BookingGuestAccess.Token.v1");

    public string Protect(string token) => _protector.Protect(token);

    public string Unprotect(string protectedToken) =>
        _protector.Unprotect(protectedToken);
}
