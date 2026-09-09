using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class HousekeepingTask
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid? BookingId { get; set; }
    public HousekeepingTaskType Type { get; set; }
    public OperationalTaskStatus Status { get; set; } = OperationalTaskStatus.Pending;
    public WorkPriority Priority { get; set; } = WorkPriority.Normal;
    public string Notes { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AssignedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public bool? InspectionPassed { get; set; }
    public Room? Room { get; set; }
    public Booking? Booking { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public ApplicationUser? AssignedToUser { get; set; }
    public ApplicationUser? CompletedByUser { get; set; }
}
