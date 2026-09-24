using FluentAssertions;
using MooreHotels.Infrastructure.Media;
using SkiaSharp;
using Xunit;

namespace MooreHotels.UnitTests.Media;

public sealed class ImageProcessorTests
{
    private static byte[] CreateJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    [Fact]
    public void CreateVariants_returns_thumb_medium_and_large_as_webp()
    {
        var variants = ImageProcessor.CreateVariants(CreateJpeg(2400, 1600));

        variants.Select(v => v.Name).Should().Equal("thumb", "medium", "large");
        foreach (var variant in variants)
        {
            using var codec = SKCodec.Create(new MemoryStream(variant.Bytes));
            codec.EncodedFormat.Should().Be(SKEncodedImageFormat.Webp);
        }
    }

    [Fact]
    public void CreateVariants_caps_longest_edge_and_preserves_aspect_ratio()
    {
        var variants = ImageProcessor.CreateVariants(CreateJpeg(2400, 1600));

        variants.Single(v => v.Name == "thumb").Width.Should().Be(400);
        variants.Single(v => v.Name == "thumb").Height.Should().Be(267);
        variants.Single(v => v.Name == "medium").Width.Should().Be(800);
        variants.Single(v => v.Name == "medium").Height.Should().Be(533);
        variants.Single(v => v.Name == "large").Width.Should().Be(1600);
        variants.Single(v => v.Name == "large").Height.Should().Be(1067);
    }

    [Fact]
    public void CreateVariants_never_upscales_small_images()
    {
        var variants = ImageProcessor.CreateVariants(CreateJpeg(300, 200));

        variants.Should().OnlyContain(v => v.Width == 300 && v.Height == 200);
    }

    [Fact]
    public void CreateVariants_rejects_corrupt_input()
    {
        var act = () => ImageProcessor.CreateVariants([1, 2, 3, 4, 5]);

        act.Should().Throw<InvalidOperationException>();
    }
}
