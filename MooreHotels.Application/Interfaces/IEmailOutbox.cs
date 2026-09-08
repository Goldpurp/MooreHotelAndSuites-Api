using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Interfaces;

public interface IEmailOutbox
{
    EmailOutboxMessage Create(
        string emailTemplate,
        string recipient,
        object payload,
        string? dataSubjectGuestId = null);
    T ReadPayload<T>(EmailOutboxMessage message) where T : class;
    Task EnqueueAsync(
        string emailTemplate,
        string recipient,
        object payload,
        string? dataSubjectGuestId = null,
        CancellationToken cancellationToken = default);
}
