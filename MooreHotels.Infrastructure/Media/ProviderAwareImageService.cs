using Microsoft.AspNetCore.Http;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.Infrastructure.Media;

/// <summary>
/// Uploads through the selected provider, but keeps newly created R2 assets
/// deletable after a configuration-only rollback to Cloudinary. Retain this
/// adapter and the R2 credentials during rollback; do not roll back the binary.
/// Legacy IDs follow the active provider and must have their URLs restored by
/// the migration's conditional rollback before changing that provider.
/// </summary>
public sealed class ProviderAwareImageService(
    IImageService activeProvider,
    Func<IImageService> r2Provider) : IImageService
{
    public const string R2PublicIdPrefix = "r2/";

    public Task<ImageUploadResult?> UploadImageAsync(IFormFile file, string folder = "general") =>
        activeProvider.UploadImageAsync(file, folder);

    public Task<List<ImageUploadResult>> UploadMultipleAsync(List<IFormFile> files, string folder = "rooms") =>
        activeProvider.UploadMultipleAsync(files, folder);

    public Task<bool> DeleteImageAsync(string publicId) =>
        (publicId?.Trim().StartsWith(R2PublicIdPrefix, StringComparison.Ordinal) == true
            ? r2Provider()
            : activeProvider).DeleteImageAsync(publicId!);
}
