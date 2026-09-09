using System.Text.Encodings.Web;

namespace MooreHotels.Application.Common;

public static class FrontendLinkBuilder
{
    public static string WithFragment(
        string baseUrl,
        string path,
        IEnumerable<KeyValuePair<string, string?>> values)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("The frontend URL is not configured.");
        }

        var fragment = string.Join(
            '&',
            values
                .Where(pair => pair.Value is not null)
                .Select(pair =>
                    $"{UrlEncoder.Default.Encode(pair.Key)}={UrlEncoder.Default.Encode(pair.Value!)}"));

        return $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}#{fragment}";
    }
}
