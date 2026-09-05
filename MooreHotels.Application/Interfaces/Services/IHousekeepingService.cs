using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Interfaces.Services;

public interface IHousekeepingService
{
    Task<IReadOnlyList<HousekeepingTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default);
    Task<HousekeepingTaskDto> CreateTaskAsync(CreateHousekeepingTaskRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<HousekeepingTaskDto> UpdateTaskAsync(Guid id, UpdateHousekeepingTaskRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MaintenanceWorkOrderDto>> GetWorkOrdersAsync(CancellationToken cancellationToken = default);
    Task<MaintenanceWorkOrderDto> CreateWorkOrderAsync(CreateMaintenanceWorkOrderRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<MaintenanceWorkOrderDto> UpdateWorkOrderAsync(Guid id, UpdateMaintenanceWorkOrderRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task CreateCheckoutTasksAsync(Booking booking, Guid actorId, CancellationToken cancellationToken = default);
}
