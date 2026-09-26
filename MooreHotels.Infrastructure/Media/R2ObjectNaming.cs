namespace MooreHotels.Infrastructure.Media;

public static class R2ObjectNaming
{
    public const string OriginalName = "original";

    public static string OriginalKey(string publicId) =>
        $"{NormalizePublicId(publicId)}-{OriginalName}";

    public static string VariantKey(string publicId, string variantName)
    {
        if (string.IsNullOrWhiteSpace(variantName) ||
            variantName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("The variant name is invalid.", nameof(variantName));
        }

        return $"{NormalizePublicId(publicId)}-{variantName}.webp";
    }

    public static string PublicUrl(
        string publicBaseUrl,
        string publicId,
        string variantName = "medium")
    {
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
        {
            throw new ArgumentException(
                "The R2 public base URL must be an HTTPS URL without credentials, a query, or a fragment.",
                nameof(publicBaseUrl));
        }

        var key = VariantKey(publicId, variantName);
        return $"{publicBaseUrl.TrimEnd('/')}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";
    }

    private static string NormalizePublicId(string publicId)
    {
        publicId = publicId?.Trim() ?? string.Empty;
        if (publicId.Length == 0 || publicId.Length > 512 ||
            publicId.StartsWith('/') || publicId.EndsWith('/') ||
            publicId.Contains("//", StringComparison.Ordinal) ||
            publicId.Split('/').Any(segment => segment is "." or "..") ||
            publicId.Any(char.IsControl))
        {
            throw new ArgumentException("The public ID is invalid.", nameof(publicId));
        }

        return publicId;
    }
}
