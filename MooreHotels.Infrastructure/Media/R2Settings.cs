namespace MooreHotels.Infrastructure.Services;

public sealed class R2Settings
{
    public required string AccountId { get; set; }
    public required string AccessKeyId { get; set; }
    public required string SecretAccessKey { get; set; }
    public required string BucketName { get; set; }

    /// <summary>
    /// Public origin objects are served from — an R2 custom domain in
    /// Production, or the bucket's default r2.dev URL while testing.
    /// No trailing slash.
    /// </summary>
    public required string PublicBaseUrl { get; set; }
}
