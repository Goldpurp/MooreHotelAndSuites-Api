namespace MooreHotels.Domain.Entities;

public sealed class MediaAsset
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string PublicId { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public Guid? UploadedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ApplicationUser? UploadedByUser { get; set; }
}
