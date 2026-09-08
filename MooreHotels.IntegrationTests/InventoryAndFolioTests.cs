using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.DTOs.Pricing;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Domain.Common;
using MooreHotels.Infrastructure.Persistence;
using Npgsql;
using System.Text.Json;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class InventoryAndFolioTests
{
    private readonly ManualTransferTestFixture _fixture;

    public InventoryAndFolioTests(ManualTransferTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Room_type_quote_books_multiple_units_and_defers_physical_assignment()
    {
        var (roomType, rooms) = await CreateInventoryAsync(2, 80000m);
        var checkIn = DateTime.UtcNow.Date.AddDays(45);
        var checkOut = checkIn.AddDays(3);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingService>();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingService>();

        var quote = await pricing.CreateQuoteAsync(new CreatePricingQuoteRequest(
            RoomId: null,
            CheckIn: checkIn,
            CheckOut: checkOut,
            AdultCount: 4,
            ChildCount: 0,
            RoomTypeId: roomType.Id,
            RoomQuantity: 2));

        Assert.Equal(2, quote.RoomQuantity);
        Assert.Equal(480000m, quote.RoomSubtotal);
        Assert.All(
            quote.Lines.Where(line => line.Type == PricingLineType.RoomNight),
            line => Assert.Equal(2, line.Quantity));

        var unique = Guid.NewGuid().ToString("N");
        var created = await bookings.CreateBookingAsync(new CreateBookingRequest(
            RoomId: null,
            GuestFirstName: "Group",
            GuestLastName: "Guest",
            GuestEmail: $"group-{unique}@example.test",
            GuestPhone: "+2348000000011",
            CheckIn: checkIn,
            CheckOut: checkOut,
            AdultCount: 4,
            ChildCount: 0,
            PaymentMethod: PaymentMethod.DirectTransfer,
            Notes: "Two-room reservation",
            QuoteId: quote.QuoteId,
            QuoteToken: quote.QuoteToken,
            RoomTypeId: roomType.Id,
            RoomQuantity: 2));

        Assert.Null(created.RoomId);
        Assert.Equal(2, created.Rooms!.Count);
        Assert.All(created.Rooms, room => Assert.Null(room.AssignedRoomId));
        Assert.Equal(created.Amount, created.Folio!.AmountDue);

        var availability = await inventory.GetAvailabilityAsync(
            roomType.Id,
            DateOnly.FromDateTime(checkIn),
            DateOnly.FromDateTime(checkOut),
            1);
        Assert.False(availability.Available);
        Assert.Equal(0, availability.AvailableUnits);

        var assignment = await inventory.AssignRoomAsync(
            created.Id,
            created.Rooms[0].Id,
            new AssignReservationRoomRequest(rooms[0].Id, "Assigned for arrival preparation."),
            _fixture.Admin.Id);
        Assert.Equal(rooms[0].Id, assignment.AssignedRoomId);

        var stored = await _fixture.WithDbAsync(db => db.Bookings.AsNoTracking()
            .SingleAsync(booking => booking.Id == created.Id));
        Assert.Equal(rooms[0].Id, stored.RoomId);
    }

    [Fact]
    public async Task Folio_supports_split_payment_later_charge_void_refund_and_is_immutable()
    {
        var booking = await _fixture.CreateBookingAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var folios = scope.ServiceProvider.GetRequiredService<IFolioService>();

        var firstPayment = await folios.PostPaymentAsync(
            booking.BookingCode,
            new PostFolioPaymentRequest(
                40000m, "BankTransfer", $"PART-{Guid.NewGuid():N}",
                $"part-one-{Guid.NewGuid():N}", ConfirmReservation: false),
            _fixture.Admin.Id);
        Assert.Equal(60000m, firstPayment.AmountDue);

        var secondPayment = await folios.PostPaymentAsync(
            booking.BookingCode,
            new PostFolioPaymentRequest(
                60000m, "Cash", $"PART-{Guid.NewGuid():N}",
                $"part-two-{Guid.NewGuid():N}"),
            _fixture.Manager.Id);
        Assert.Equal(0m, secondPayment.AmountDue);

        var firstPaymentEntry = secondPayment.Entries.Single(entry =>
            entry.Type == FolioEntryType.Payment && entry.Amount == 40000m);
        var voidedPayment = await folios.VoidEntryAsync(
            booking.BookingCode,
            firstPaymentEntry.Id,
            new VoidFolioEntryRequest(
                "The first transfer was posted to the wrong reservation.",
                $"void-payment-{Guid.NewGuid():N}"),
            _fixture.Admin.Id);
        Assert.Equal(40000m, voidedPayment.AmountDue);
        Assert.Equal(60000m, voidedPayment.Payments);

        var replacementPayment = await folios.PostPaymentAsync(
            booking.BookingCode,
            new PostFolioPaymentRequest(
                40000m, "BankTransfer", $"REPLACEMENT-{Guid.NewGuid():N}",
                $"replacement-{Guid.NewGuid():N}"),
            _fixture.Manager.Id);
        Assert.Equal(0m, replacementPayment.AmountDue);
        Assert.Equal(100000m, replacementPayment.Payments);

        var charged = await folios.PostChargeAsync(
            booking.BookingCode,
            new PostFolioChargeRequest(
                FolioEntryType.AddOnCharge, 15000m, "Airport transfer", "FrontDesk",
                null, $"charge-{Guid.NewGuid():N}"),
            _fixture.Admin.Id);
        Assert.Equal(15000m, charged.AmountDue);
        var charge = charged.Entries.Single(entry => entry.Description == "Airport transfer");

        var settled = await folios.PostPaymentAsync(
            booking.BookingCode,
            new PostFolioPaymentRequest(
                15000m, "Cash", $"PART-{Guid.NewGuid():N}",
                $"part-three-{Guid.NewGuid():N}"),
            _fixture.Manager.Id);
        Assert.Equal(0m, settled.AmountDue);

        var voided = await folios.VoidEntryAsync(
            booking.BookingCode,
            charge.Id,
            new VoidFolioEntryRequest(
                "Service was cancelled before delivery.",
                $"void-{Guid.NewGuid():N}"),
            _fixture.Admin.Id);
        Assert.Equal(15000m, voided.GuestCredit);

        var scopedDb = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var trackedBooking = await scopedDb.Bookings
            .Include(item => item.Folio).ThenInclude(folio => folio!.Entries)
            .SingleAsync(item => item.Id == booking.Id);
        await folios.ApplyRefundAsync(
            trackedBooking,
            15000m,
            $"REF-{Guid.NewGuid():N}",
            "Cash",
            "Returned after the void.",
            _fixture.Manager.Id);
        var refunded = await folios.GetByBookingCodeAsync(booking.BookingCode);
        Assert.Equal(0m, refunded.Balance);
        Assert.Equal(15000m, refunded.Refunds);

        await using var immutableScope = _fixture.Services.CreateAsyncScope();
        var db = immutableScope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var original = await db.FolioEntries.FirstAsync(entry => entry.FolioId == refunded.Id);
        original.Description = "Tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        await using var databaseScope = _fixture.Services.CreateAsyncScope();
        var database = databaseScope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var databaseError = await Assert.ThrowsAsync<PostgresException>(() =>
            database.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM folio_entries WHERE \"Id\" = {original.Id}"));
        Assert.Equal("CK_folio_entries_immutable", databaseError.ConstraintName);
    }

    [Fact]
    public async Task Concurrent_room_type_allocations_cannot_oversell_inventory()
    {
        var (roomType, _) = await CreateInventoryAsync(2, 60000m);
        var checkIn = DateTime.UtcNow.Date.AddDays(60).AddHours(13);
        var checkOut = checkIn.AddDays(2).AddHours(-2);

        async Task<bool> TryBookAsync(int number)
        {
            await using var scope = _fixture.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider
                .GetRequiredService<MooreHotels.Application.Interfaces.Repositories.IBookingRepository>();
            var unique = Guid.NewGuid().ToString("N").ToUpperInvariant();
            var guest = new Guest
            {
                Id = $"GS-{unique[..16]}",
                FirstName = "Concurrent",
                LastName = $"Guest{number}",
                Email = $"concurrent-{unique}@example.test",
                Phone = "+2348000000012"
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                BookingCode = $"MHS{unique[..6]}",
                RoomTypeId = roomType.Id,
                RoomQuantity = 2,
                GuestId = guest.Id,
                CheckIn = checkIn,
                CheckOut = checkOut,
                AdultCount = 4,
                Status = BookingStatus.Pending,
                Currency = "NGN",
                RoomSubtotal = 240000m,
                Amount = 240000m,
                PaymentStatus = PaymentStatus.AwaitingVerification,
                PaymentMethod = PaymentMethod.DirectTransfer,
                StatusHistoryJson = "[]",
                CreatedAt = DateTime.UtcNow
            };
            for (var sequence = 1; sequence <= 2; sequence++)
            {
                booking.ReservationRooms.Add(new ReservationRoom
                {
                    Id = Guid.NewGuid(),
                    BookingId = booking.Id,
                    RoomTypeId = roomType.Id,
                    RoomTypeCode = roomType.Code,
                    RoomTypeName = roomType.Name,
                    Sequence = sequence,
                    CreatedAtUtc = booking.CreatedAt
                });
            }
            booking.Folio = FolioAccounting.CreateInitial(booking, booking.CreatedAt);
            try
            {
                await repository.AddAsync(booking, guest);
                return true;
            }
            catch (BadRequestException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(TryBookAsync(1), TryBookAsync(2));
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
    }

    [Fact]
    public async Task Concurrent_inventory_closures_cannot_collectively_oversell_inventory()
    {
        var (roomType, _) = await CreateInventoryAsync(2, 60000m);
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(70));
        var end = start.AddDays(3);

        async Task<bool> TryCloseAsync(int number)
        {
            await using var scope = _fixture.Services.CreateAsyncScope();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            try
            {
                await inventory.CreateClosureAsync(
                    new CreateInventoryClosureRequest(
                        roomType.Id,
                        null,
                        start,
                        end,
                        2,
                        $"Concurrent capacity closure {number}."),
                    _fixture.Admin.Id);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(TryCloseAsync(1), TryCloseAsync(2));
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
    }

    [Fact]
    public async Task Inventory_export_idempotency_replays_original_snapshot_after_inventory_changes()
    {
        var (roomType, _) = await CreateInventoryAsync(2, 60000m);
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(75));
        var end = start.AddDays(2);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var channels = scope.ServiceProvider.GetRequiredService<IChannelManagementService>();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
        var channel = await channels.SaveChannelAsync(
            null,
            new SaveDistributionChannelRequest(
                $"INV-{Guid.NewGuid():N}"[..20],
                "Inventory replay channel",
                true),
            _fixture.Admin.Id);
        var request = new QueueChannelInventoryRequest(
            roomType.Id,
            start,
            end,
            $"inventory-{Guid.NewGuid():N}");

        var original = await channels.QueueInventoryAsync(
            channel.Id,
            request,
            _fixture.Admin.Id);
        await inventory.CreateClosureAsync(
            new CreateInventoryClosureRequest(
                roomType.Id,
                null,
                start,
                end.AddDays(1),
                1,
                "Change availability after the original export."),
            _fixture.Admin.Id);
        var replay = await channels.QueueInventoryAsync(
            channel.Id,
            request,
            _fixture.Admin.Id);

        Assert.Equal(original.Id, replay.Id);
        using var originalJson = JsonDocument.Parse(original.PayloadJson);
        using var replayJson = JsonDocument.Parse(replay.PayloadJson);
        Assert.True(JsonElement.DeepEquals(
            originalJson.RootElement,
            replayJson.RootElement));
    }

    private async Task<(RoomType RoomType, Room[] Rooms)> CreateInventoryAsync(
        int unitCount,
        decimal nightlyRate)
    {
        var unique = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var type = new RoomType
        {
            Id = Guid.NewGuid(),
            Code = $"GRP-{unique[..8]}",
            Name = "Group Standard",
            Category = RoomCategory.Standard,
            BaseOccupancy = 1,
            MaxOccupancy = 2,
            BasePricePerNight = nightlyRate,
            Description = "Multi-room integration inventory.",
            Amenities = ["Wi-Fi"]
        };
        var rooms = Enumerable.Range(1, unitCount).Select(index => new Room
        {
            Id = Guid.NewGuid(),
            RoomTypeId = type.Id,
            RoomNumber = $"G-{unique[..5]}-{index}",
            Name = $"Group Room {index}",
            Category = RoomCategory.Standard,
            Floor = PropertyFloor.FirstFloor,
            Status = RoomStatus.Available,
            PricePerNight = nightlyRate,
            Capacity = 2,
            Size = "30 sqm",
            IsOnline = true,
            Description = "Group inventory test room.",
            Amenities = ["Wi-Fi"]
        }).ToArray();
        await _fixture.WithDbAsync(async db =>
        {
            db.RoomTypes.Add(type);
            db.Rooms.AddRange(rooms);
            await db.SaveChangesAsync();
            return true;
        });
        return (type, rooms);
    }
}
