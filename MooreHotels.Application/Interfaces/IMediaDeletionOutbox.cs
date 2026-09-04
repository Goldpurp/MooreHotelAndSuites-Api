using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Interfaces;

public interface IMediaDeletionOutbox
{
    MediaDeletionJob Create(string publicId, string sourceType, string sourceId);
}
