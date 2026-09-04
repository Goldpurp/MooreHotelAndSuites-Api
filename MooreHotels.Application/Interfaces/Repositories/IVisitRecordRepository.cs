
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Interfaces.Repositories;

public interface IVisitRecordRepository
{
    Task<IEnumerable<VisitRecord>> GetAllAsync();
    Task<PagedResult<VisitRecord>> GetPagedRecordsAsync(
        int pageNumber = 1,
        int pageSize = 20,
        string? search = null,
        CancellationToken cancellationToken = default);
    Task AddAsync(VisitRecord record);
}
