
namespace MooreHotels.Application.DTOs;

public record DashboardOverviewDto(
    DashboardKpis Kpis,
    List<RevenuePoint> RevenueDynamics,
    AssetStatusDistribution AssetStatus,
    List<ActiveOperationDto> ActiveOperations);

public record DashboardKpis(
    decimal NetRevenue,
    double OccupancyRate,
    int ActiveGuests,
    decimal AvgNightlyRate,
    decimal RevenueGrowthPercentage,
    double OccupancyGrowthPercentage);

public record RevenuePoint(string Date, decimal Value);

public record AssetStatusDistribution(
    int Occupied,
    int Available,
    int Cleaning,
    int Maintenance,
    int Dirty = 0,
    int Clean = 0,
    int Inspected = 0,
    int OutOfOrder = 0);

public record ActiveOperationDto(
    string GuestName,
    string GuestAvatar,
    string Folio,
    string AssetType,
    string UnitNumber,
    string Stage, // e.g. "CHECKED IN"
    decimal Amount,
    string PaymentStatus);
