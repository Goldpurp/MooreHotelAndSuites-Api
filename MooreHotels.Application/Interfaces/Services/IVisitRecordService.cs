
using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IVisitRecordService
{
    Task<IEnumerable<VisitRecordDto>> GetAllRecordsAsync();
    Task<PagedResult<VisitRecordDto>> GetPagedRecordsAsync(int pageNumber = 1, int pageSize = 20, string? search = null);
    Task CreateRecordAsync(string bookingCode, string action, string authorizedBy);
}
