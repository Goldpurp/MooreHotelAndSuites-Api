using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Application.Exceptions;

namespace MooreHotels.Application.Services;

public class GuestService : IGuestService
{
    private readonly IGuestRepository _guestRepo;
    public GuestService(IGuestRepository guestRepo) => _guestRepo = guestRepo;

    public async Task<IEnumerable<GuestDto>> GetAllGuestsAsync()
    {
        var guests = await _guestRepo.GetAllAsync();
        return guests.Select(MapToDto);
    }

    public async Task<PagedResult<GuestDto>> GetPagedGuestsAsync(int pageNumber = 1, int pageSize = 20, string? search = null)
    {
        ValidateSearch(search);
        var paged = await _guestRepo.GetPagedGuestsAsync(pageNumber, pageSize, search);
        var mapped = paged.Items.Select(MapToDto).ToList();
        return PagedResult<GuestDto>.Create(mapped, paged.TotalCount, paged.PageNumber, paged.PageSize);
    }

    public async Task<IEnumerable<GuestDto>> SearchGuestsAsync(string term)
    {
        ValidateSearch(term);
        var guests = await _guestRepo.SearchAsync(term);
        return guests.Select(MapToDto);
    }

    public async Task<GuestDto?> GetGuestByIdAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 40 || id.Any(char.IsControl))
            throw new BadRequestException("Guest identifier is invalid.");
        var guest = await _guestRepo.GetByIdAsync(id);
        return guest != null ? MapToDto(guest) : null;
    }

    private static GuestDto MapToDto(Guest g) => new(
        g.Id, g.FirstName, g.LastName, g.Email, g.Phone, g.AvatarUrl, g.CreatedAt);

    private static void ValidateSearch(string? search)
    {
        if (search?.Length > 120 || search?.Any(char.IsControl) == true)
            throw new BadRequestException("Guest search is invalid or too long.");
    }
}
