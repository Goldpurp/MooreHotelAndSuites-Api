using FluentAssertions;
using MooreHotels.Infrastructure.Media;
using Xunit;

namespace MooreHotels.UnitTests.Media;

public sealed class R2ObjectNamingTests
{
    [Fact]
    public void Keys_preserve_cloudinary_public_id_and_use_stable_suffixes()
    {
        R2ObjectNaming.OriginalKey("MooreHotels/rooms/example")
            .Should().Be("MooreHotels/rooms/example-original");
        R2ObjectNaming.VariantKey("MooreHotels/rooms/example", "medium")
            .Should().Be("MooreHotels/rooms/example-medium.webp");
    }

    [Fact]
    public void PublicUrl_escapes_each_path_segment_without_destroying_folders()
    {
        R2ObjectNaming.PublicUrl(
                "https://media.example.com/",
                "MooreHotels/rooms/sea view")
            .Should().Be(
                "https://media.example.com/MooreHotels/rooms/sea%20view-medium.webp");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/rooted")]
    [InlineData("folder/../escape")]
    [InlineData("folder//asset")]
    public void Keys_reject_unsafe_public_ids(string publicId)
    {
        var act = () => R2ObjectNaming.OriginalKey(publicId);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("http://media.example.com")]
    [InlineData("https://user:secret@media.example.com")]
    [InlineData("https://media.example.com?token=value")]
    public void PublicUrl_rejects_unsafe_public_origins(string origin)
    {
        var act = () => R2ObjectNaming.PublicUrl(origin, "rooms/example");

        act.Should().Throw<ArgumentException>();
    }
}
