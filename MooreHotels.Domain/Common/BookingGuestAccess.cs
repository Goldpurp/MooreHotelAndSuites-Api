using System.Security.Cryptography;
using System.Text;

namespace MooreHotels.Domain.Common;

public static class BookingGuestAccess
{
    public static string GenerateToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Verify(string? token, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expectedHash))
            return false;

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
