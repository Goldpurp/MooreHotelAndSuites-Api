using MooreHotels.Application.Interfaces;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Infrastructure.Services;

public sealed class MediaDeletionOutbox : IMediaDeletionOutbox
{
    public MediaDeletionJob Create(string publicId, string sourceType, string sourceId)
    {
        var now = DateTime.UtcNow;
        return new MediaDeletionJob
        {
            Id = Guid.NewGuid(),
            PublicId = publicId.Trim(),
            SourceType = sourceType.Trim(),
            SourceId = sourceId.Trim(),
            AttemptCount = 0,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now
        };
    }
}
