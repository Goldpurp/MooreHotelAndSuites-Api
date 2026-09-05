using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class MaintenanceWorkOrder
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid? InventoryClosureId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public WorkPriority Priority { get; set; } = WorkPriority.Normal;
    public MaintenanceWorkOrderStatus Status { get; set; } = MaintenanceWorkOrderStatus.Open;
    public DateOnly OutOfOrderFrom { get; set; }
    public DateOnly OutOfOrderUntil { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolutionNotes { get; set; }
    public Room? Room { get; set; }
    public RoomInventoryClosure? InventoryClosure { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public ApplicationUser? AssignedToUser { get; set; }
    public ApplicationUser? ResolvedByUser { get; set; }
}
