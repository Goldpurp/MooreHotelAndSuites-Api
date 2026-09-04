using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IGuestService
{
    Task<IEnumerable<GuestDto>> GetAllGuestsAsync();
    Task<PagedResult<GuestDto>> GetPagedGuestsAsync(int pageNumber = 1, int pageSize = 20, string? search = null);
    Task<IEnumerable<GuestDto>> SearchGuestsAsync(string term);
    Task<GuestDto?> GetGuestByIdAsync(string id);
}
