using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class Folio
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string Currency { get; set; } = "NGN";
    public FolioStatus Status { get; set; } = FolioStatus.Open;
    public DateTime OpenedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAtUtc { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public Booking? Booking { get; set; }
    public ApplicationUser? ClosedByUser { get; set; }
    public ICollection<FolioEntry> Entries { get; set; } = new List<FolioEntry>();
}
