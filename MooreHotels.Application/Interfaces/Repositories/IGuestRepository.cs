using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Interfaces.Repositories;

public interface IGuestRepository
{
    Task<Guest?> GetByIdAsync(string id);
    Task<Guest?> GetByEmailAndNameAsync(string email, string firstName, string lastName);
    Task<IEnumerable<Guest>> SearchAsync(string term);
    Task<IEnumerable<Guest>> GetAllAsync();
    Task AddAsync(Guest guest);
    Task UpdateAsync(Guest guest);
}
