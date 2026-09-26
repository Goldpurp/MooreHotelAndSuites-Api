using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Infrastructure.Media;
using Xunit;

namespace MooreHotels.UnitTests.Media;

public sealed class ProviderAwareImageServiceTests
{
    [Fact]
    public async Task R2_deletion_still_targets_R2_after_provider_rollback()
    {
        var cloudinary = new Mock<IImageService>(MockBehavior.Strict);
        var r2 = new Mock<IImageService>(MockBehavior.Strict);
        r2.Setup(service => service.DeleteImageAsync("r2/rooms/new-image")).ReturnsAsync(true);
        var adapter = new ProviderAwareImageService(cloudinary.Object, () => r2.Object);

        (await adapter.DeleteImageAsync("r2/rooms/new-image")).Should().BeTrue();
        cloudinary.VerifyNoOtherCalls();
        r2.VerifyAll();
    }

    [Theory]
    [InlineData("MooreHotels/rooms/legacy")]
    [InlineData("rooms/r2/legacy")]
    public async Task Legacy_deletion_follows_selected_provider_without_initializing_R2(string id)
    {
        var active = new Mock<IImageService>(MockBehavior.Strict);
        active.Setup(service => service.DeleteImageAsync(id)).ReturnsAsync(true);
        var adapter = new ProviderAwareImageService(active.Object,
            () => throw new InvalidOperationException("R2 must not be resolved for legacy deletion."));

        (await adapter.DeleteImageAsync(id)).Should().BeTrue();
        active.VerifyAll();
    }

    [Fact]
    public async Task Uploads_keep_the_selected_provider_and_folder()
    {
        var active = new Mock<IImageService>(MockBehavior.Strict);
        var file = new Mock<IFormFile>().Object;
        var files = new List<IFormFile> { file };
        var result = new ImageUploadResult("r2/avatars/new", "https://media.example.com/new-medium.webp");
        active.Setup(service => service.UploadImageAsync(file, "avatars")).ReturnsAsync(result);
        active.Setup(service => service.UploadMultipleAsync(files, "rooms")).ReturnsAsync([result]);
        var adapter = new ProviderAwareImageService(active.Object,
            () => throw new InvalidOperationException("Deletion provider must not be resolved for upload."));

        (await adapter.UploadImageAsync(file, "avatars")).Should().BeSameAs(result);
        (await adapter.UploadMultipleAsync(files, "rooms")).Should().ContainSingle().Which.Should().BeSameAs(result);
        active.VerifyAll();
    }
}
