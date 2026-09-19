using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.DTOs.Pricing;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class CompleteHotelOperationsTests
{
    private readonly ManualTransferTestFixture _fixture;
    public CompleteHotelOperationsTests(ManualTransferTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Cancellation_inside_24_hours_keeps_the_full_room_charge()
    {
        var booking = await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed,
            checkInUtc: DateTime.UtcNow.AddHours(6),
            checkOutUtc: DateTime.UtcNow.AddDays(2));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingService>();
        var result = await bookings.CancelBookingAsync(
            booking.Id, _fixture.Manager.Id, "Guest requested a late cancellation.");

        Assert.Equal(100000m, result.ReservationPolicy!.CancellationPenaltyAmount);
        Assert.Equal(0m, result.Folio!.GuestCredit);
        Assert.Equal(PaymentStatus.PartiallyPaid, result.PaymentStatus);
    }

    [Fact]
    public async Task Cancellation_before_24_hours_refunds_the_full_room_charge()
    {
        var booking = await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed,
            checkInUtc: DateTime.UtcNow.AddDays(2),
            checkOutUtc: DateTime.UtcNow.AddDays(4));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingService>();
        var result = await bookings.CancelBookingAsync(
            booking.Id, _fixture.Manager.Id, "Guest cancelled before the cutoff.");

        Assert.Equal(0m, result.ReservationPolicy!.CancellationPenaltyAmount);
        Assert.Equal(100000m, result.Folio!.GuestCredit);
        Assert.Equal(PaymentStatus.RefundPending, result.PaymentStatus);
    }

    [Fact]
    public async Task Reservation_amendment_reprices_reallocates_and_preserves_append_only_history()
    {
        var booking = await _fixture.CreateBookingAsync();
        var start = DateTime.UtcNow.Date.AddDays(20);
        var end = start.AddDays(3);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingService>();
        var amendments = scope.ServiceProvider.GetRequiredService<IReservationAmendmentService>();
        var unchangedQuote = await pricing.CreateAmendmentQuoteAsync(
            booking.Id,
            new CreatePricingQuoteRequest(
                booking.RoomId, booking.CheckIn, booking.CheckOut, 2, 0),
            _fixture.Manager.Id);
        Assert.Equal(booking.RoomId, unchangedQuote.RoomId);

        var quote = await pricing.CreateAmendmentQuoteAsync(booking.Id, new CreatePricingQuoteRequest(
            booking.RoomId, start, end, 2, 0), _fixture.Manager.Id);

        var result = await amendments.AmendAsync(
            booking.Id,
            new AmendReservationRequest(
                quote.QuoteId, quote.QuoteToken, booking.RoomId, booking.RoomTypeId,
                1, start, end, 2, 0, "Guest extended the reservation by one night."),
            _fixture.Manager.Id);

        Assert.Equal(quote.TotalAmount, result.NewAmount);
        Assert.Equal(3,
            DateOnly.FromDateTime(result.CheckOut).DayNumber -
            DateOnly.FromDateTime(result.CheckIn).DayNumber);
        Assert.Equal(quote.TotalAmount, result.Folio.AmountDue);
        var history = await amendments.GetHistoryAsync(booking.Id);
        Assert.Single(history);
        Assert.True(await _fixture.WithDbAsync(db => db.FolioEntries.AnyAsync(entry =>
            entry.FolioId == result.Folio.Id && entry.Type == FolioEntryType.Void)));
    }

    [Fact]
    public async Task Checkout_creates_cleaning_task_and_inspection_releases_room()
    {
        var housekeeper = await _fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var engineer = await _fixture.CreateUserAsync(UserRole.Staff, "Engineering");
        var booking = await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.CheckedIn);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingService>();
        var housekeeping = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        await bookings.UpdateStatusAsync(booking.Id, BookingStatus.CheckedOut, _fixture.Staff.Id);

        var cleaning = Assert.Single(
            await housekeeping.GetTasksAsync(),
            item => item.BookingId == booking.Id && item.Type == HousekeepingTaskType.CheckoutCleaning);
        Assert.Equal(RoomStatus.Dirty, await RoomStatusAsync(booking.RoomId!.Value));
        await housekeeping.UpdateTaskAsync(
            cleaning.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.InProgress, housekeeper.Id, null, null),
            housekeeper.Id);
        await housekeeping.UpdateTaskAsync(
            cleaning.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Completed, housekeeper.Id, null, null),
            housekeeper.Id);
        var inspection = Assert.Single(
            await housekeeping.GetTasksAsync(),
            item => item.BookingId == booking.Id && item.Type == HousekeepingTaskType.Inspection &&
                    item.Status != OperationalTaskStatus.Completed);
        await housekeeping.UpdateTaskAsync(
            inspection.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.InProgress, _fixture.Manager.Id, null, null),
            _fixture.Manager.Id);
        await housekeeping.UpdateTaskAsync(
            inspection.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Completed, _fixture.Manager.Id, true, null),
            _fixture.Manager.Id);
        Assert.Equal(RoomStatus.Available, await RoomStatusAsync(booking.RoomId.Value));

        var taskCount = (await housekeeping.GetTasksAsync()).Count;
        await housekeeping.UpdateTaskAsync(
            inspection.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Completed, _fixture.Manager.Id, true, null),
            _fixture.Manager.Id);
        Assert.Equal(taskCount, (await housekeeping.GetTasksAsync()).Count);

        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var workOrder = await housekeeping.CreateWorkOrderAsync(
            new CreateMaintenanceWorkOrderRequest(
                booking.RoomId.Value,
                "Repair room climate control",
                "Replace the failed climate-control component and verify operation.",
                WorkPriority.High,
                time.Today.AddDays(-1),
                time.Today.AddDays(5),
                engineer.Id),
            _fixture.Manager.Id);
        Assert.Equal(RoomStatus.OutOfOrder, await RoomStatusAsync(booking.RoomId.Value));
        await housekeeping.UpdateWorkOrderAsync(
            workOrder.Id,
            new UpdateMaintenanceWorkOrderRequest(
                MaintenanceWorkOrderStatus.Resolved,
                engineer.Id,
                "Component replaced and the room passed engineering checks."),
            _fixture.Manager.Id);
        var closure = await _fixture.WithDbAsync(async db =>
            await db.RoomInventoryClosures.AsNoTracking()
                .SingleAsync(item => item.Id == workOrder.InventoryClosureId));
        Assert.True(closure.IsActive);
        Assert.Equal(time.Today.AddDays(1), closure.EndDate);
        Assert.Equal(RoomStatus.Dirty, await RoomStatusAsync(booking.RoomId.Value));
    }

    [Fact]
    public async Task Guest_merge_moves_stays_and_preserves_verified_identity_evidence()
    {
        var duplicateBooking = await _fixture.CreateBookingAsync();
        var primaryId = $"GS-{Guid.NewGuid():N}"[..19].ToUpperInvariant();
        await _fixture.WithDbAsync(async db =>
        {
            var duplicate = await db.Guests.SingleAsync(item => item.Id == duplicateBooking.GuestId);
            duplicate.NormalizedEmail = duplicate.Email.ToLowerInvariant();
            duplicate.EmailVerifiedAtUtc = DateTime.UtcNow;
            db.Guests.Add(new Guest
            {
                Id = primaryId,
                FirstName = duplicate.FirstName,
                LastName = duplicate.LastName,
                Email = duplicate.Email,
                NormalizedEmail = duplicate.Email.ToLowerInvariant(),
                Phone = "+2348111111111",
                NormalizedPhone = "+2348111111111",
                EmailVerifiedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return true;
        });
        await using var scope = _fixture.Services.CreateAsyncScope();
        var crm = scope.ServiceProvider.GetRequiredService<IGuestCrmService>();
        await Assert.ThrowsAsync<BadRequestException>(() => crm.MergeAsync(
            new MergeGuestRequest(
                primaryId,
                duplicateBooking.GuestId,
                "VerifiedEmail",
                "Staff wrote a permanent narrative containing possible guest data."),
            _fixture.Admin.Id));
        var merged = await crm.MergeAsync(new MergeGuestRequest(
            primaryId, duplicateBooking.GuestId, "VerifiedEmail",
            "CRM-MERGE-VERIFIED-EMAIL-001"), _fixture.Admin.Id);

        Assert.Equal(1, merged.MovedBookingCount);
        var profile = await crm.GetProfileAsync(primaryId, true);
        Assert.Contains(profile.StayHistory, item => item.BookingId == duplicateBooking.Id);
    }

    [Fact]
    public async Task Channel_events_are_idempotent_and_reconciliation_exposes_unmapped_reservations()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var channels = scope.ServiceProvider.GetRequiredService<IChannelManagementService>();
        var channel = await channels.SaveChannelAsync(null,
            new SaveDistributionChannelRequest($"TEST-{Guid.NewGuid():N}"[..20], "Test channel", true),
            _fixture.Admin.Id);
        var key = $"reservation-{Guid.NewGuid():N}";
        var request = new ReceiveChannelEventRequest(
            "RESERVATION_CREATED", key, $"EXT-{Guid.NewGuid():N}", "{\"status\":\"confirmed\"}", DateTime.UtcNow);
        var first = await channels.ReceiveEventAsync(channel.Code, request);
        var repeated = await channels.ReceiveEventAsync(channel.Code, request);

        Assert.Equal(first.Id, repeated.Id);
        var reconciliation = await channels.GetReconciliationAsync(channel.Id);
        Assert.True(reconciliation.PendingEvents >= 1);
        Assert.True(reconciliation.UnmappedReservationEvents >= 1);
    }

    [Fact]
    public async Task Operational_report_uses_hotel_day_and_night_audit_is_idempotent()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var reporting = scope.ServiceProvider.GetRequiredService<IOperationalReportingService>();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var today = time.Today;
        await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.CheckedOut,
            checkInUtc: time.GetCheckInUtc(today.AddDays(-2).ToDateTime(TimeOnly.MinValue)),
            checkOutUtc: time.GetCheckOutUtc(today.ToDateTime(TimeOnly.MinValue)));
        var report = await reporting.GetReportAsync(today.AddDays(-2), today);
        Assert.True(report.OccupiedRoomNights >= 2);
        Assert.True(report.RoomRevenue > 0);
        Assert.True(report.Adr > 0);
        var first = await reporting.CloseNightAuditAsync(today.AddDays(-1), _fixture.Manager.Id);
        var repeated = await reporting.CloseNightAuditAsync(today.AddDays(-1), _fixture.Manager.Id);
        Assert.Equal(first.Id, repeated.Id);
    }

    private Task<RoomStatus> RoomStatusAsync(Guid roomId) =>
        _fixture.WithDbAsync(async db => await db.Rooms.AsNoTracking()
            .Where(item => item.Id == roomId).Select(item => item.Status).SingleAsync());
}
