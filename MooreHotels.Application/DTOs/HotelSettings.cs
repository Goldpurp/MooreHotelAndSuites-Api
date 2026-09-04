namespace MooreHotels.Application.DTOs;

public sealed class HotelSettings
{
    public string Name { get; init; } = string.Empty;
    public string Tagline { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string SupportEmail { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string TimeZoneId { get; init; } = "Africa/Lagos";
    public int CheckInHour { get; init; } = 14;
    public int CheckOutHour { get; init; } = 12;
}
