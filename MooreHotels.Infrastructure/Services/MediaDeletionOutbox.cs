using MooreHotels.Application.Interfaces;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Infrastructure.Services;

public sealed class MediaDeletionOutbox : IMediaDeletionOutbox
{
    public MediaDeletionJob Create(string publicId, string sourceType, string sourceId)
    {
        publicId = RequireValue(publicId, nameof(publicId), 512);
        sourceType = RequireValue(sourceType, nameof(sourceType), 50);
        sourceId = RequireValue(sourceId, nameof(sourceId), 160);
        var now = DateTime.UtcNow;
        return new MediaDeletionJob
        {
            Id = Guid.NewGuid(),
            PublicId = publicId,
            SourceType = sourceType,
            SourceId = sourceId,
            AttemptCount = 0,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now
        };
    }

    private static string RequireValue(string? value, string name, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength)
        {
            throw new ArgumentException("The media deletion request is invalid.", name);
        }

        return normalized;
    }
}
