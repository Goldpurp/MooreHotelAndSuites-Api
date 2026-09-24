using SkiaSharp;

namespace MooreHotels.Infrastructure.Media;

public sealed record ImageVariant(string Name, byte[] Bytes, int Width, int Height);

/// <summary>
/// Re-encodes an uploaded image into a fixed set of WebP variants, replacing
/// Cloudinary's upload-time eager transforms and delivery-time q_auto/w_*
/// transforms with variants generated once at upload time.
/// </summary>
public static class ImageProcessor
{
    public sealed record VariantSpec(string Name, int MaxEdge, int Quality);

    // Sizes chosen to cover the responsive breakpoints the frontend previously
    // requested via Cloudinary's on-the-fly w_* transform. Quality values are
    // the WebP analog of Cloudinary's q_auto:good — visually lossless, meaningfully smaller.
    public static readonly IReadOnlyList<VariantSpec> Variants =
    [
        new VariantSpec("thumb", 400, 80),
        new VariantSpec("medium", 800, 88),
        new VariantSpec("large", 1600, 88),
    ];

    public static List<ImageVariant> CreateVariants(byte[] sourceBytes)
    {
        using var oriented = DecodeWithOrientation(sourceBytes);
        var results = new List<ImageVariant>(Variants.Count);
        foreach (var spec in Variants)
        {
            using var resized = ResizeToMaxEdge(oriented, spec.MaxEdge);
            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, spec.Quality);
            if (encoded is null)
                throw new InvalidOperationException("The image could not be encoded.");
            results.Add(new ImageVariant(spec.Name, encoded.ToArray(), resized.Width, resized.Height));
        }
        return results;
    }

    private static SKBitmap DecodeWithOrientation(byte[] bytes)
    {
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            throw new InvalidOperationException("The image could not be decoded.");

        var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null)
            throw new InvalidOperationException("The image could not be decoded.");

        try
        {
            return ApplyOrientation(bitmap, codec.EncodedOrigin);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    // Skia decodes raw encoded pixels without honoring the EXIF orientation
    // tag; phone-captured photos are frequently stored sideways/upside down
    // unless this is corrected before resizing/encoding.
    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return bitmap.Copy();

        var swapsDimensions = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.LeftBottom or SKEncodedOrigin.RightBottom;
        var width = swapsDimensions ? bitmap.Height : bitmap.Width;
        var height = swapsDimensions ? bitmap.Width : bitmap.Height;

        var oriented = new SKBitmap(width, height, bitmap.ColorType, bitmap.AlphaType);
        using (var canvas = new SKCanvas(oriented))
        {
            canvas.Clear(SKColors.Transparent);
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:
                    canvas.Translate(width, 0);
                    canvas.Scale(-1, 1);
                    break;
                case SKEncodedOrigin.BottomRight:
                    canvas.Translate(width, height);
                    canvas.RotateDegrees(180);
                    break;
                case SKEncodedOrigin.BottomLeft:
                    canvas.Translate(0, height);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.LeftTop:
                    canvas.RotateDegrees(90);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.RightTop:
                    canvas.RotateDegrees(90);
                    canvas.Translate(0, -height);
                    break;
                case SKEncodedOrigin.RightBottom:
                    canvas.Translate(width, 0);
                    canvas.RotateDegrees(90);
                    canvas.Scale(-1, 1);
                    break;
                case SKEncodedOrigin.LeftBottom:
                    canvas.Translate(0, height);
                    canvas.RotateDegrees(-90);
                    break;
            }

            canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default, paint: null);
        }

        return oriented;
    }

    private static SKBitmap ResizeToMaxEdge(SKBitmap source, int maxEdge)
    {
        var longestEdge = Math.Max(source.Width, source.Height);
        if (longestEdge <= maxEdge)
        {
            // Never upscale beyond the source's own resolution.
            return source.Copy();
        }

        var scale = (double)maxEdge / longestEdge;
        var targetWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        var info = new SKImageInfo(targetWidth, targetHeight, source.ColorType, source.AlphaType);
        var resized = source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized is null)
            throw new InvalidOperationException("The image could not be resized.");
        return resized;
    }
}
