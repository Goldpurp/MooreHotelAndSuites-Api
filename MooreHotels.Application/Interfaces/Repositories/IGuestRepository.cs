using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Interfaces.Repositories;

public interface IGuestRepository
{
    Task<Guest?> GetByIdAsync(string id);
    Task<Guest?> GetByEmailAndNameAsync(string email, string firstName, string lastName);
    Task<IEnumerable<Guest>> SearchAsync(string term);
    Task<IEnumerable<Guest>> GetAllAsync();
    Task<PagedResult<Guest>> GetPagedGuestsAsync(
        int pageNumber = 1,
        int pageSize = 20,
        string? search = null,
        CancellationToken cancellationToken = default);
    Task AddAsync(Guest guest);
    Task UpdateAsync(Guest guest);
}
