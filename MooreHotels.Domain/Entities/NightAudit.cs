namespace MooreHotels.Domain.Entities;

public sealed class NightAudit
{
    public Guid Id { get; set; }
    public DateOnly BusinessDate { get; set; }
    public int AvailableRoomNights { get; set; }
    public int OccupiedRoomNights { get; set; }
    public decimal RoomRevenue { get; set; }
    public decimal Payments { get; set; }
    public decimal Refunds { get; set; }
    public decimal Receivables { get; set; }
    public decimal GuestCredits { get; set; }
    public decimal Adr { get; set; }
    public decimal RevPar { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public DateTime ClosedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid ClosedByUserId { get; set; }
    public ApplicationUser? ClosedByUser { get; set; }
}
