namespace MooreHotels.Domain.Common;

public sealed class ReservationPolicySettings
{
    public string Version { get; init; } = "2026-09-13";
    public int FreeCancellationHours { get; init; } = 24;
    public decimal CancellationPenaltyPercent { get; init; } = 100m;
    public decimal DepositPercent { get; init; } = 30m;
    public decimal NoShowPenaltyPercent { get; init; } = 100m;
}
