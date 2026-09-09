namespace MooreHotels.Domain.Entities;

public sealed class ChannelReservationMapping
{
    public Guid Id { get; set; }
    public Guid ChannelId { get; set; }
    public string ExternalReservationId { get; set; } = string.Empty;
    public Guid BookingId { get; set; }
    public DateTime LinkedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid LinkedByUserId { get; set; }
    public DistributionChannel? Channel { get; set; }
    public Booking? Booking { get; set; }
    public ApplicationUser? LinkedByUser { get; set; }
}
