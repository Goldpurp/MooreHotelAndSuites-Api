using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public sealed record ReservationPolicySnapshotDto(
    string Version,
    int FreeCancellationHours,
    decimal CancellationPenaltyPercent,
    decimal DepositPercent,
    decimal NoShowPenaltyPercent,
    decimal MinimumDepositAmount,
    decimal CancellationPenaltyAmount,
    decimal NoShowPenaltyAmount);

public sealed record AmendReservationRequest(
    [Required] Guid QuoteId,
    [Required, StringLength(200)] string QuoteToken,
    Guid? RoomId,
    [Required] Guid RoomTypeId,
    [Range(1, 10)] int RoomQuantity,
    [Required] DateTime CheckIn,
    [Required] DateTime CheckOut,
    [Range(1, 20)] int AdultCount,
    [Range(0, 20)] int ChildCount,
    [Required, StringLength(500, MinimumLength = 10)] string Reason);

public sealed record ReservationAmendmentDto(
    Guid Id,
    Guid BookingId,
    decimal PreviousAmount,
    decimal NewAmount,
    decimal PriceDifference,
    string Reason,
    DateTime AmendedAtUtc,
    Guid AmendedByUserId,
    Guid RoomTypeId,
    int RoomQuantity,
    DateTime CheckIn,
    DateTime CheckOut,
    int AdultCount,
    int ChildCount,
    FolioSummaryDto Folio);

public sealed record HousekeepingTaskDto(
    Guid Id,
    Guid RoomId,
    string RoomNumber,
    Guid? BookingId,
    string? BookingCode,
    HousekeepingTaskType Type,
    OperationalTaskStatus Status,
    WorkPriority Priority,
    string Notes,
    Guid? AssignedToUserId,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    bool? InspectionPassed);

public sealed record CreateHousekeepingTaskRequest(
    [Required] Guid RoomId,
    Guid? BookingId,
    HousekeepingTaskType Type,
    WorkPriority Priority,
    [StringLength(1000)] string? Notes);

public sealed record UpdateHousekeepingTaskRequest(
    OperationalTaskStatus Status,
    Guid? AssignedToUserId,
    bool? InspectionPassed,
    [StringLength(1000)] string? Notes);

public sealed record MaintenanceWorkOrderDto(
    Guid Id,
    Guid RoomId,
    string RoomNumber,
    Guid? InventoryClosureId,
    string Title,
    string Description,
    WorkPriority Priority,
    MaintenanceWorkOrderStatus Status,
    DateOnly OutOfOrderFrom,
    DateOnly OutOfOrderUntil,
    Guid? AssignedToUserId,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? ResolvedAtUtc,
    string? ResolutionNotes);

public sealed record CreateMaintenanceWorkOrderRequest(
    [Required] Guid RoomId,
    [Required, StringLength(160, MinimumLength = 4)] string Title,
    [Required, StringLength(2000, MinimumLength = 10)] string Description,
    WorkPriority Priority,
    [Required] DateOnly OutOfOrderFrom,
    [Required] DateOnly OutOfOrderUntil,
    Guid? AssignedToUserId);

public sealed record UpdateMaintenanceWorkOrderRequest(
    MaintenanceWorkOrderStatus Status,
    Guid? AssignedToUserId,
    [StringLength(1000)] string? ResolutionNotes);

public sealed record OperationsBoardItemDto(
    Guid BookingId,
    string BookingCode,
    string GuestName,
    Guid RoomTypeId,
    string RoomTypeName,
    int RoomQuantity,
    DateTime CheckIn,
    DateTime CheckOut,
    BookingStatus Status,
    PaymentStatus PaymentStatus,
    decimal AmountDue,
    decimal GuestCredit,
    IReadOnlyList<ReservationRoomDto> Rooms);

public sealed record OperationsBoardDto(
    DateOnly BusinessDate,
    IReadOnlyList<OperationsBoardItemDto> Arrivals,
    IReadOnlyList<OperationsBoardItemDto> Departures,
    IReadOnlyList<OperationsBoardItemDto> InHouse,
    IReadOnlyList<OperationsBoardItemDto> OutstandingBalances,
    IReadOnlyList<UnassignedReservationRoomDto> UnassignedRooms,
    IReadOnlyList<HousekeepingTaskDto> Housekeeping,
    IReadOnlyList<MaintenanceWorkOrderDto> Maintenance);

public sealed record UnassignedReservationRoomDto(
    Guid BookingId,
    string BookingCode,
    string GuestName,
    ReservationRoomDto ReservationRoom);

public sealed record ReservationCalendarItemDto(
    Guid BookingId,
    string BookingCode,
    string GuestName,
    Guid RoomTypeId,
    string RoomTypeName,
    int RoomQuantity,
    DateTime CheckIn,
    DateTime CheckOut,
    BookingStatus Status,
    PaymentStatus PaymentStatus,
    decimal AmountDue,
    IReadOnlyList<string> AssignedRoomNumbers);

public sealed record OperationalReportDto(
    DateOnly FromDate,
    DateOnly ToDate,
    int AvailableRoomNights,
    int OccupiedRoomNights,
    decimal RoomRevenue,
    decimal Adr,
    decimal RevPar,
    decimal Payments,
    decimal Refunds,
    decimal Receivables,
    decimal GuestCredits,
    int ScheduledArrivals,
    int ActualCheckIns,
    int ScheduledDepartures,
    int ActualCheckOuts,
    int PendingRefundCount);

public sealed record NightAuditDto(
    Guid Id,
    DateOnly BusinessDate,
    OperationalReportDto Snapshot,
    DateTime ClosedAtUtc,
    Guid ClosedByUserId);
