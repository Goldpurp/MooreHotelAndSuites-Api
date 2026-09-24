using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Infrastructure.Media;

namespace MooreHotels.Infrastructure.Services;

/// <summary>
/// Stores media in a Cloudflare R2 bucket (S3-compatible). Each upload is
/// re-encoded into a fixed set of WebP variants by <see cref="ImageProcessor"/>
/// and stored under one logical key: "{PublicId}-{variant}.webp" per variant.
/// PublicId itself is never a literal object key, so no schema change was
/// needed when this replaced Cloudinary — see MediaAsset/RoomImage, which
/// already stored only a single Url + PublicId per image.
/// </summary>
public sealed class R2ImageService : IImageService, IDisposable
{
    private const long MaximumImageBytes = 8 * 1024 * 1024;
    private const int MaximumFilesPerRequest = 10;
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(90);
    private static readonly HashSet<string> AllowedFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "avatars", "general", "rooms", "website-assets"
        };

    private readonly AmazonS3Client _client;
    private readonly R2Settings _settings;
    private readonly ILogger<R2ImageService> _logger;

    public R2ImageService(IOptions<R2Settings> config, ILogger<R2ImageService> logger)
    {
        _settings = config.Value;
        _logger = logger;
        _client = new AmazonS3Client(
            new BasicAWSCredentials(_settings.AccessKeyId, _settings.SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = $"https://{_settings.AccountId}.r2.cloudflarestorage.com",
                AuthenticationRegion = "auto",
                ForcePathStyle = true,
                // R2 does not implement the AWS SDK v4 default chunked/streaming
                // signed-payload upload mode; each PutObjectRequest below also
                // opts out per-request (belt and suspenders — both are required).
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
    }

    public async Task<ImageUploadResult?> UploadImageAsync(IFormFile file, string folder = "general")
    {
        if (file == null || file.Length == 0) return null;
        if (file.Length > MaximumImageBytes || !AllowedFolders.Contains(folder))
            throw new BadRequestException("The image upload request is invalid.");

        byte[] sourceBytes;
        await using (var stream = file.OpenReadStream())
        await using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer);
            sourceBytes = buffer.ToArray();
        }

        List<ImageVariant> variants;
        try
        {
            variants = ImageProcessor.CreateVariants(sourceBytes);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(
                "Uploaded image could not be processed: {ExceptionType}.",
                exception.GetType().Name);
            throw new BadRequestException("The uploaded image could not be processed.");
        }

        var publicId = $"{folder.ToLowerInvariant()}/{Guid.NewGuid():N}";
        var uploaded = new List<string>(variants.Count);
        using var timeout = new CancellationTokenSource(UploadTimeout);
        try
        {
            foreach (var variant in variants)
            {
                var key = VariantKey(publicId, variant.Name);
                await _client.PutObjectAsync(
                    new PutObjectRequest
                    {
                        BucketName = _settings.BucketName,
                        Key = key,
                        InputStream = new MemoryStream(variant.Bytes),
                        ContentType = "image/webp",
                        AutoCloseStream = true,
                        // R2 rejects the SDK's default chunked/trailer-signed
                        // streaming upload; a plain signed request is required.
                        UseChunkEncoding = false,
                        DisablePayloadSigning = true,
                    },
                    timeout.Token);
                uploaded.Add(key);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogWarning(
                "R2 image upload exceeded the {TimeoutSeconds}-second provider timeout.",
                UploadTimeout.TotalSeconds);
            await CleanupAsync(uploaded);
            throw new ServiceUnavailableException("Image storage did not respond in time.") { ErrorCode = "image_upload_unavailable" };
        }
        catch (AmazonServiceException exception)
        {
            // Provider messages can contain account identifiers or signed
            // parameters. Record only the exception type, never raw text.
            _logger.LogWarning(
                "R2 image upload failed with {ExceptionType}.",
                exception.GetType().FullName);
            await CleanupAsync(uploaded);
            throw new ServiceUnavailableException("Image storage rejected the upload.") { ErrorCode = "image_upload_unavailable" };
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                "R2 image upload failed with {ExceptionType}.",
                exception.GetType().FullName);
            await CleanupAsync(uploaded);
            throw new ServiceUnavailableException("Image storage could not be reached.", exception) { ErrorCode = "image_upload_unavailable" };
        }

        var mediumUrl = $"{_settings.PublicBaseUrl.TrimEnd('/')}/{VariantKey(publicId, "medium")}";
        return new ImageUploadResult(publicId, mediumUrl);
    }

    public async Task<List<ImageUploadResult>> UploadMultipleAsync(List<IFormFile> files, string folder = "rooms")
    {
        if (files == null || files.Count == 0) return [];
        if (files.Count > MaximumFilesPerRequest)
            throw new BadRequestException("Too many images were supplied.");

        var tasks = files.Select(file => UploadImageAsync(file, folder)).ToArray();
        try
        {
            var results = await Task.WhenAll(tasks);
            return results.Where(result => result is not null).Cast<ImageUploadResult>().ToList();
        }
        catch
        {
            // Task.WhenAll waits for every upload. Remove any successful
            // objects so a partial provider failure cannot leave untracked assets.
            var succeeded = tasks
                .Where(task => task.Status == TaskStatus.RanToCompletion && task.Result is not null)
                .Select(task => task.Result!);
            await Task.WhenAll(succeeded.Select(result => DeleteImageAsync(result.PublicId)));
            throw;
        }
    }

    public async Task<bool> DeleteImageAsync(string publicId)
    {
        publicId = publicId?.Trim() ?? string.Empty;
        if (publicId.Length == 0 || publicId.Length > 512) return false;

        var keys = VariantNames.Select(name => VariantKey(publicId, name));
        // R2's multi-object DeleteObjectsAsync is unreliable with this SDK, so
        // each variant is deleted individually. S3-compatible delete is
        // idempotent — deleting a key that never existed still succeeds,
        // matching Cloudinary's "ok" or "not found" result either way.
        await Task.WhenAll(keys.Select(key => _client.DeleteObjectAsync(_settings.BucketName, key)));
        return true;
    }

    private async Task CleanupAsync(List<string> keys)
    {
        if (keys.Count == 0) return;
        try
        {
            await Task.WhenAll(keys.Select(key => _client.DeleteObjectAsync(_settings.BucketName, key)));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Failed to clean up a partially completed R2 upload: {ExceptionType}.",
                exception.GetType().Name);
        }
    }

    private static readonly string[] VariantNames = ImageProcessor.Variants.Select(v => v.Name).ToArray();

    private static string VariantKey(string publicId, string variantName) => $"{publicId}-{variantName}.webp";

    public void Dispose() => _client.Dispose();
}
