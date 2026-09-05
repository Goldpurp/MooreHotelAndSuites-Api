using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IGuestCrmService
{
    Task<GuestCrmProfileDto> GetProfileAsync(string guestId, bool includeSensitiveNotes, CancellationToken cancellationToken = default);
    Task<GuestCrmProfileDto> UpdatePreferencesAsync(string guestId, UpdateGuestPreferencesRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<GuestNoteDto> AddNoteAsync(string guestId, AddGuestNoteRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task VerifyContactAsync(string guestId, VerifyGuestContactRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuestDuplicateCandidateDto>> FindDuplicatesAsync(string guestId, CancellationToken cancellationToken = default);
    Task<GuestMergeDto> MergeAsync(MergeGuestRequest request, Guid actorId, CancellationToken cancellationToken = default);
}
