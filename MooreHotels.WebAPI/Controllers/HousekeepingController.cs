using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/housekeeping")]
[Authorize(Policy = HotelAuthorization.HousekeepingManage)]
public sealed class HousekeepingController : ControllerBase
{
    private readonly IHousekeepingService _service;
    public HousekeepingController(IHousekeepingService service) => _service = service;

    [HttpGet("tasks")]
    public async Task<ActionResult<IReadOnlyList<HousekeepingTaskDto>>> GetTasks(CancellationToken ct) =>
        Ok(await _service.GetTasksAsync(ct));

    [HttpPost("tasks")]
    public async Task<ActionResult<HousekeepingTaskDto>> CreateTask(
        [FromBody] CreateHousekeepingTaskRequest request, CancellationToken ct) =>
        Ok(await _service.CreateTaskAsync(request, ActorId(), ct));

    [HttpPut("tasks/{id:guid}")]
    public async Task<ActionResult<HousekeepingTaskDto>> UpdateTask(
        Guid id, [FromBody] UpdateHousekeepingTaskRequest request, CancellationToken ct) =>
        Ok(await _service.UpdateTaskAsync(id, request, ActorId(), ct));

    private Guid ActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id : throw new UnauthorizedAccessException("The authenticated actor is invalid.");
}

[ApiController]
[Route("api/maintenance")]
[Authorize(Policy = HotelAuthorization.MaintenanceManage)]
public sealed class MaintenanceController : ControllerBase
{
    private readonly IHousekeepingService _service;
    public MaintenanceController(IHousekeepingService service) => _service = service;

    [HttpGet("work-orders")]
    public async Task<ActionResult<IReadOnlyList<MaintenanceWorkOrderDto>>> Get(CancellationToken ct) =>
        Ok(await _service.GetWorkOrdersAsync(ct));

    [HttpPost("work-orders")]
    public async Task<ActionResult<MaintenanceWorkOrderDto>> Create(
        [FromBody] CreateMaintenanceWorkOrderRequest request, CancellationToken ct) =>
        Ok(await _service.CreateWorkOrderAsync(request, ActorId(), ct));

    [HttpPut("work-orders/{id:guid}")]
    public async Task<ActionResult<MaintenanceWorkOrderDto>> Update(
        Guid id, [FromBody] UpdateMaintenanceWorkOrderRequest request, CancellationToken ct) =>
        Ok(await _service.UpdateWorkOrderAsync(id, request, ActorId(), ct));

    private Guid ActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id : throw new UnauthorizedAccessException("The authenticated actor is invalid.");
}
