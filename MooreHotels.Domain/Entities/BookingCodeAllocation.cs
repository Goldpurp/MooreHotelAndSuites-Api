namespace MooreHotels.Domain.Entities;

/// <summary>
/// Reserves a public booking code before external payment initialization starts.
/// The primary key makes allocation collision-safe across concurrent API instances.
/// </summary>
public sealed class BookingCodeAllocation
{
    public string Code { get; set; } = string.Empty;
    public DateTime AllocatedAtUtc { get; set; }
}
