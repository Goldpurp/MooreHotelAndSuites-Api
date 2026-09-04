namespace MooreHotels.Application.Interfaces;

public interface IBookingGuestAccessProtector
{
    string Protect(string token);
    string Unprotect(string protectedToken);
}
