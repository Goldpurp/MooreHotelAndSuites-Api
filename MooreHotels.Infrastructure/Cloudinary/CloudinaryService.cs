using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyImageResult = MooreHotels.Application.DTOs.ImageUploadResult;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.Infrastructure.Services;

public class CloudinaryService : IImageService
{
    private const long MaximumImageBytes = 8 * 1024 * 1024;
    private const int MaximumFilesPerRequest = 10;
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(90);
    private static readonly HashSet<string> AllowedFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "avatars", "general", "rooms", "website-assets"
        };
    private readonly Cloudinary _cloudinary;
    private readonly ILogger<CloudinaryService> _logger;

    public CloudinaryService(
        IOptions<CloudinarySettings> config,
        ILogger<CloudinaryService> logger)
    {
        var settings = config.Value;
        _cloudinary = new Cloudinary(new Account(settings.CloudName, settings.ApiKey, settings.ApiSecret));
        _logger = logger;
    }

    public async Task<MyImageResult?> UploadImageAsync(IFormFile file, string folder = "general")
    {
        if (file == null || file.Length == 0) return null;
        if (file.Length > MaximumImageBytes || !AllowedFolders.Contains(folder))
            throw new BadRequestException("The image upload request is invalid.");

        await using var stream = file.OpenReadStream();
        var safeFileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName.Length > 200)
            safeFileName = "upload";
        var uploadParams = BuildUploadParameters(safeFileName, stream, folder);

        using var timeout = new CancellationTokenSource(UploadTimeout);
        ImageUploadResult result;
        try
        {
            result = await _cloudinary.UploadAsync(uploadParams, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Cloudinary image upload exceeded the {TimeoutSeconds}-second provider timeout.",
                UploadTimeout.TotalSeconds);
            throw new ServiceUnavailableException("Image storage did not respond in time.") { ErrorCode = "image_upload_unavailable" };
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                "Cloudinary image upload failed with {ExceptionType}.",
                exception.GetType().FullName);
            throw new ServiceUnavailableException("Image storage could not be reached.", exception) { ErrorCode = "image_upload_unavailable" };
        }

        if (result.Error != null ||
            string.IsNullOrWhiteSpace(result.PublicId) ||
            result.PublicId.Length > 512 ||
            result.SecureUrl is null ||
            !Uri.TryCreate(result.SecureUrl.ToString(), UriKind.Absolute, out var secureUrl) ||
            secureUrl.Scheme != Uri.UriSchemeHttps)
        {
            // Provider messages can contain account identifiers or signed parameters.
            // Record only structured status and response validity, never raw messages.
            _logger.LogWarning(
                "Cloudinary upload rejected: HTTP {ProviderStatus}; provider error {HasProviderError}; public ID present {HasPublicId}; secure URL present {HasSecureUrl}.",
                (int)result.StatusCode, result.Error is not null,
                !string.IsNullOrWhiteSpace(result.PublicId), result.SecureUrl is not null);
            throw new ServiceUnavailableException("Image storage rejected the upload.") { ErrorCode = "image_upload_unavailable" };
        }

        return new MyImageResult(result.PublicId, secureUrl.ToString());
    }

    internal static ImageUploadParams BuildUploadParameters(string safeFileName, Stream stream, string folder)
    {
        return new ImageUploadParams
        {
            File = new FileDescription(safeFileName, stream),
            Folder = $"MooreHotels/{folder.ToLowerInvariant()}",

            // 1. PRIMARY TRANSFORMATION (Main high-res view)
            // Automatic format selection belongs on delivery URLs, never incoming uploads.
            Transformation = new Transformation()
                .Width(1200).Height(800).Crop("limit")
                .Quality("auto"),

            // 2. EAGER TRANSFORMATIONS (Generated instantly in the background)
            EagerTransforms = new List<Transformation>
            {
                // Dashboard Thumbnail: 300x300 square crop, AI-centered on the subject
                new Transformation().Width(300).Height(300).Crop("fill").Gravity("auto").Quality("auto"),

                // Mobile Version: Optimized for smaller screens
                new Transformation().Width(640).Crop("scale").Quality("auto")
            },

            // 3. PERFORMANCE: Don't wait for thumbnails to finish to return the main URL
            EagerAsync = true
        };

    }

    public async Task<List<MyImageResult>> UploadMultipleAsync(List<IFormFile> files, string folder = "rooms")
    {
        if (files == null || files.Count == 0) return new List<MyImageResult>();
        if (files.Count > MaximumFilesPerRequest)
            throw new BadRequestException("Too many images were supplied.");

        var tasks = files.Select(file => UploadImageAsync(file, folder)).ToArray();
        try
        {
            var results = await Task.WhenAll(tasks);
            return results.Where(result => result is not null).Cast<MyImageResult>().ToList();
        }
        catch
        {
            // Task.WhenAll waits for every upload. Remove any successful objects
            // so a partial provider failure cannot leave untracked assets.
            var uploaded = tasks
                .Where(task => task.Status == TaskStatus.RanToCompletion && task.Result is not null)
                .Select(task => task.Result!);
            await Task.WhenAll(uploaded.Select(result => DeleteImageAsync(result.PublicId)));
            throw;
        }
    }

    public async Task<bool> DeleteImageAsync(string publicId)
    {
        publicId = publicId?.Trim() ?? string.Empty;
        if (publicId.Length == 0 || publicId.Length > 512) return false;

        var deleteParams = new DeletionParams(publicId)
        {
            Invalidate = true
        };

        var result = await _cloudinary.DestroyAsync(deleteParams);
        return result.Result is "ok" or "not found";
    }

}
