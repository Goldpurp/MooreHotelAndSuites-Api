using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;

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

    public async Task<IEnumerable<GuestDto>> SearchGuestsAsync(string term)
    {
        var guests = await _guestRepo.SearchAsync(term);
        return guests.Select(MapToDto);
    }

    public async Task<GuestDto?> GetGuestByIdAsync(string id)
    {
        var guest = await _guestRepo.GetByIdAsync(id);
        return guest != null ? MapToDto(guest) : null;
    }

    private static GuestDto MapToDto(Guest g) => new(
        g.Id, g.FirstName, g.LastName, g.Email, g.Phone, g.AvatarUrl, g.CreatedAt);
}
