using MooreHotels.Domain.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MooreHotels.WebAPI.Services;

public static class MonnifyWebhookSecurity
{
    public static bool VerifySignature(
        string requestBody,
        string? signature,
        string secretKey)
    {
        if (string.IsNullOrWhiteSpace(signature) ||
            signature.Length != 128 ||
            string.IsNullOrWhiteSpace(secretKey))
        {
            return false;
        }

        try
        {
            var supplied = Convert.FromHexString(signature);
            var expected = HMACSHA512.HashData(
                Encoding.UTF8.GetBytes(secretKey),
                Encoding.UTF8.GetBytes(requestBody));
            return supplied.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(
                       supplied,
                       expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsAllowedSource(
        IPAddress? remoteAddress,
        MonnifySettings settings)
    {
        if (!settings.EnforceWebhookIpAllowlist) return true;
        if (remoteAddress is null) return false;

        remoteAddress = Normalize(remoteAddress);
        return settings.AllowedWebhookIpAddresses.Any(value =>
            IPAddress.TryParse(value, out var configured) &&
            Normalize(configured).Equals(remoteAddress));
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
}
