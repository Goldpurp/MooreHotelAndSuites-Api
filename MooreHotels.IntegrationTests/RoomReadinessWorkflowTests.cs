using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.DTOs.Pricing;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Services;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class RoomReadinessWorkflowTests(ManualTransferTestFixture fixture)
{
    public static IEnumerable<object[]> SaleCases()
    {
        foreach (var status in Enum.GetValues<RoomStatus>())
            foreach (var future in new[] { false, true })
                foreach (var byType in new[] { false, true })
                    yield return [status, future, byType];
    }

    [Theory]
    [MemberData(nameof(SaleCases))]
    public async Task Quotes_and_bookings_require_release_today_but_preserve_future_sales(
        RoomStatus status, bool future, bool byType)
    {
        var room = await fixture.CreateRoomAsync();
        await SetStatus(room.Id, status);
        await using var scope = fixture.Services.CreateAsyncScope();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingService>();
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingService>();
        var start = time.Today.AddDays(future ? 10 : 0).ToDateTime(TimeOnly.MinValue);
        var expected = status is not (RoomStatus.Maintenance or RoomStatus.OutOfOrder) &&
                       (future || status == RoomStatus.Available);
        var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
        Assert.Equal(expected, (await roomService.CheckAvailabilityAsync(room.Id, start, start.AddDays(1))).Available);
        var search = await roomService.SearchRoomsAsync(new RoomSearchRequest(start, start.AddDays(1)));
        Assert.Equal(expected, search.Any(result => result.Id == room.Id));
        var availability = await scope.ServiceProvider.GetRequiredService<IInventoryService>().GetAvailabilityAsync(
            room.RoomTypeId, DateOnly.FromDateTime(start), DateOnly.FromDateTime(start.AddDays(1)), 1);
        Assert.Equal(expected, availability.Available);
        var quoteRequest = new CreatePricingQuoteRequest(byType ? null : room.Id, start, start.AddDays(1),
            1, 0, RoomTypeId: byType ? room.RoomTypeId : null);
        var request = Request(room, start, byType);
        if (!expected)
        {
            await Assert.ThrowsAsync<BadRequestException>(() => pricing.CreateQuoteAsync(quoteRequest));
            await Assert.ThrowsAsync<BadRequestException>(() => bookings.CreateBookingAsync(request));
            Assert.False(await fixture.WithDbAsync(db => db.Bookings.AnyAsync(b => b.RoomTypeId == room.RoomTypeId)));
            return;
        }
        var quote = await pricing.CreateQuoteAsync(quoteRequest);
        var created = await bookings.CreateBookingAsync(request with { QuoteId = quote.QuoteId, QuoteToken = quote.QuoteToken });
        Assert.Equal(BookingStatus.Pending, created.Status);
        Assert.True(await fixture.WithDbAsync(db => db.Bookings.AnyAsync(b => b.Id == created.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_quote_does_not_authorize_booking_after_room_readiness_changes(bool byType)
    {
        var room = await fixture.CreateRoomAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var start = scope.ServiceProvider.GetRequiredService<IHotelTimeService>().Today.ToDateTime(TimeOnly.MinValue);
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingService>();
        var quote = await pricing.CreateQuoteAsync(new CreatePricingQuoteRequest(byType ? null : room.Id,
            start, start.AddDays(1), 1, 0, RoomTypeId: byType ? room.RoomTypeId : null));
        await SetStatus(room.Id, RoomStatus.Dirty);
        var request = Request(room, start, byType) with { QuoteId = quote.QuoteId, QuoteToken = quote.QuoteToken };
        await Assert.ThrowsAsync<BadRequestException>(() => scope.ServiceProvider
            .GetRequiredService<IBookingService>().CreateBookingAsync(request));
        Assert.False(await fixture.WithDbAsync(db => db.Bookings.AnyAsync(b => b.RoomTypeId == room.RoomTypeId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assigning_and_confirming_a_room_enforces_arrival_readiness(bool future)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var start = time.GetCheckInUtc(time.Today.AddDays(future ? 10 : 0).ToDateTime(TimeOnly.MinValue));
        var booking = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported,
            checkInUtc: start, checkOutUtc: start.AddDays(2));
        await SetStatus(booking.RoomId!.Value, RoomStatus.Clean);
        var unit = await fixture.WithDbAsync(db => db.ReservationRooms.SingleAsync(r => r.BookingId == booking.Id));
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
        var reviews = scope.ServiceProvider.GetRequiredService<PaymentReviewService>();
        Task<ReservationRoomDto> Assign() => inventory.AssignRoomAsync(booking.Id, unit.Id,
            new AssignReservationRoomRequest(booking.RoomId.Value, "Prepare guest room assignment."), fixture.Admin.Id);
        Task<PaymentReviewResult> Review() => reviews.ResolveAsync(booking.BookingCode,
            new ResolveTransferRequest("Confirm", "VERIFY", "Disposable QA statement review.",
                $"BANK-{Guid.NewGuid():N}", booking.Amount), fixture.Admin.Id, default);
        if (future)
        {
            await Assign();
            await Review();
        }
        else
        {
            await Assert.ThrowsAsync<BadRequestException>(Assign);
            await Assert.ThrowsAsync<BadRequestException>(Review);
            Assert.Equal(PaymentStatus.PaymentReported, await fixture.WithDbAsync(db => db.Bookings
                .Where(b => b.Id == booking.Id).Select(b => b.PaymentStatus).SingleAsync()));
        }
        Assert.Equal(RoomStatus.Clean, await fixture.WithDbAsync(db => db.Rooms
            .Where(r => r.Id == booking.RoomId).Select(r => r.Status).SingleAsync()));
    }

    [Fact]
    public async Task Existing_reservations_still_hold_capacity_when_their_assigned_room_becomes_unready()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var start = time.GetCheckInUtc(time.Today.ToDateTime(TimeOnly.MinValue));
        var held = await fixture.CreateBookingAsync(bookingStatus: BookingStatus.Confirmed,
            checkInUtc: start, checkOutUtc: start.AddDays(1));
        await SetStatus(held.RoomId!.Value, RoomStatus.Cleaning);
        var ready = await fixture.CreateRoomAsync();
        await fixture.WithDbAsync(async db =>
        {
            (await db.Rooms.SingleAsync(r => r.Id == ready.Id)).RoomTypeId = held.RoomTypeId;
            return await db.SaveChangesAsync();
        });
        var availability = await scope.ServiceProvider.GetRequiredService<IInventoryService>()
            .GetAvailabilityAsync(held.RoomTypeId, time.Today, time.Today.AddDays(1), 1);
        Assert.Equal(0, availability.AvailableUnits);
        var request = Request(ready, time.Today.ToDateTime(TimeOnly.MinValue), true) with { RoomTypeId = held.RoomTypeId };
        await Assert.ThrowsAsync<BadRequestException>(() => scope.ServiceProvider
            .GetRequiredService<IBookingService>().CreateBookingAsync(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Amendment_rechecks_readiness_after_the_quote_without_changing_the_original_stay(bool byType)
    {
        var booking = await fixture.CreateBookingAsync();
        var originalCheckIn = await fixture.WithDbAsync(db => db.Bookings
            .Where(b => b.Id == booking.Id).Select(b => b.CheckIn).SingleAsync());
        await using var scope = fixture.Services.CreateAsyncScope();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var start = time.Today.ToDateTime(TimeOnly.MinValue);
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingService>();
        var quote = await pricing.CreateAmendmentQuoteAsync(booking.Id,
            new CreatePricingQuoteRequest(byType ? null : booking.RoomId, start, start.AddDays(2), 1, 0,
                RoomTypeId: byType ? booking.RoomTypeId : null), fixture.Admin.Id);
        await SetStatus(booking.RoomId!.Value, RoomStatus.Cleaning);
        var amendments = scope.ServiceProvider.GetRequiredService<IReservationAmendmentService>();
        var error = await Record.ExceptionAsync(() => amendments.AmendAsync(booking.Id,
            new AmendReservationRequest(quote.QuoteId, quote.QuoteToken, byType ? null : booking.RoomId,
                booking.RoomTypeId, 1, start, start.AddDays(2), 1, 0, "Move this reservation to today's arrival."),
            fixture.Admin.Id));
        Assert.True(error is BadRequestException or ConflictException, error?.ToString() ?? "Unsafe amendment was accepted.");
        Assert.Equal(originalCheckIn, await fixture.WithDbAsync(db => db.Bookings
            .Where(b => b.Id == booking.Id).Select(b => b.CheckIn).SingleAsync()));
        Assert.Empty(await amendments.GetHistoryAsync(booking.Id));
        Assert.Null(await fixture.WithDbAsync(db => db.BookingQuotes
            .Where(q => q.Id == quote.QuoteId).Select(q => q.ConsumedAtUtc).SingleAsync()));
    }

    [Fact]
    public async Task Concurrent_no_show_requests_produce_one_notice_and_one_transition()
    {
        var booking = await fixture.CreateBookingAsync(bookingStatus: BookingStatus.Confirmed,
            checkInUtc: DateTime.UtcNow.AddHours(-1));
        async Task<HttpStatusCode> Send()
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/bookings/{booking.Id}/status?status=NoShow");
            request.Headers.Add("X-Moore-App-Environment", "local");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Admin.Token);
            using var response = await fixture.Client.SendAsync(request);
            return response.StatusCode;
        }
        var statuses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Send()));
        Assert.Single(statuses, status => status == HttpStatusCode.OK);
        Assert.Equal(3, statuses.Count(status => status == HttpStatusCode.BadRequest));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.EmailOutboxMessages.CountAsync(e =>
            e.DataSubjectGuestId == booking.GuestId && e.Template == "Cancellation")));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.AuditLogs.CountAsync(a =>
            a.EntityId == booking.Id.ToString() && a.Action == "LIFECYCLE_TRANSITION")));
    }

    private Task<int> SetStatus(Guid roomId, RoomStatus status) => fixture.WithDbAsync(async db =>
    {
        (await db.Rooms.SingleAsync(r => r.Id == roomId)).Status = status;
        return await db.SaveChangesAsync();
    });

    private static CreateBookingRequest Request(Room room, DateTime start, bool byType) => new(
        RoomId: byType ? null : room.Id, GuestFirstName: "Readiness", GuestLastName: "Tester",
        GuestEmail: $"readiness-{Guid.NewGuid():N}@example.test", GuestPhone: "+2348000000011",
        CheckIn: start, CheckOut: start.AddDays(1), AdultCount: 1, ChildCount: 0,
        PaymentMethod: PaymentMethod.DirectTransfer, Notes: "Disposable readiness regression.",
        RoomTypeId: byType ? room.RoomTypeId : null);
}
