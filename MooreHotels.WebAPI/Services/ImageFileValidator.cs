namespace MooreHotels.WebAPI.Services;

public static class ImageFileValidator
{
    public const long MaxFileBytes = 8 * 1024 * 1024;
    public const long MaxMultipartRequestBytes = 26 * 1024 * 1024;
    public const int MaxFilesPerRequest = 10;

    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
            "image/avif"
        };

    public static async Task<string?> GetValidationErrorAsync(IFormFile file)
    {
        if (file.Length == 0) return "Image files cannot be empty.";
        if (file.Length > MaxFileBytes) return "Each image must be 8 MB or smaller.";
        if (!AllowedContentTypes.Contains(file.ContentType)) return "Unsupported image content type.";

        var header = new byte[16];
        await using var stream = file.OpenReadStream();
        var bytesRead = await stream.ReadAsync(header);
        if (bytesRead < 12) return "The uploaded image is invalid.";

        var isJpeg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        var isPng = header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var isWebP = header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8);
        var isAvif = header.AsSpan(4, 4).SequenceEqual("ftyp"u8) &&
                     (header.AsSpan(8, 4).SequenceEqual("avif"u8) || header.AsSpan(8, 4).SequenceEqual("avis"u8));

        return isJpeg || isPng || isWebP || isAvif
            ? null
            : "The file contents do not match a supported image format.";
    }

    public static async Task<string?> GetValidationErrorAsync(IReadOnlyCollection<IFormFile>? files)
    {
        if (files is null || files.Count == 0) return null;
        if (files.Count > MaxFilesPerRequest) return $"A maximum of {MaxFilesPerRequest} images is allowed.";
        if (files.Sum(file => file.Length) > 25 * 1024 * 1024) return "The combined image upload cannot exceed 25 MB.";

        foreach (var file in files)
        {
            var error = await GetValidationErrorAsync(file);
            if (error is not null) return error;
        }

        return null;
    }
}
