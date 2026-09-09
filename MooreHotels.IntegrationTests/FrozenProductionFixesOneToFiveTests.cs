using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class FrozenProductionFixesOneToFiveTests
{
    private readonly ManualTransferTestFixture _fixture;

    public FrozenProductionFixesOneToFiveTests(ManualTransferTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reception_preference_update_never_returns_sensitive_guest_notes()
    {
        var booking = await _fixture.CreateBookingAsync();
        var reception = await _fixture.CreateUserAsync(UserRole.Staff, "Reception");
        await _fixture.WithDbAsync(async db =>
        {
            db.GuestNotes.AddRange(
                new GuestNote
                {
                    Id = Guid.NewGuid(),
                    GuestId = booking.GuestId,
                    Body = "General arrival preference.",
                    IsSensitive = false,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedByUserId = _fixture.Admin.Id
                },
                new GuestNote
                {
                    Id = Guid.NewGuid(),
                    GuestId = booking.GuestId,
                    Body = "Management-only security note.",
                    IsSensitive = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedByUserId = _fixture.Admin.Id
                });
            await db.SaveChangesAsync();
            return true;
        });

        using (var get = Authorized(HttpMethod.Get, $"/api/guest-crm/{booking.GuestId}", reception))
        using (var response = await _fixture.Client.SendAsync(get))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var notes = json.RootElement.GetProperty("notes");
            Assert.Single(notes.EnumerateArray());
            Assert.DoesNotContain("Management-only", notes.ToString());
        }

        using var update = AuthorizedJson(
            HttpMethod.Put,
            $"/api/guest-crm/{booking.GuestId}/preferences",
            reception,
            new
            {
                preferredLanguage = "en",
                beddingPreference = "Twin",
                dietaryNotes = "No shellfish",
                accessibilityNeeds = (string?)null,
                marketingOptIn = false
            });
        using var updateResponse = await _fixture.Client.SendAsync(update);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        using var payload = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync());
        Assert.Equal("en", payload.RootElement.GetProperty("preferredLanguage").GetString());
        Assert.False(payload.RootElement.TryGetProperty("notes", out _));
    }

    [Fact]
    public async Task Folio_routes_and_service_enforce_separate_financial_duties()
    {
        var booking = await _fixture.CreateBookingAsync();
        var concierge = await _fixture.CreateUserAsync(UserRole.Staff, "Concierge");
        var frontDesk = await _fixture.CreateUserAsync(UserRole.Staff, "FrontDesk");
        var finance = await _fixture.CreateUserAsync(UserRole.Staff, "Finance");

        using (var get = Authorized(HttpMethod.Get, $"/api/folios/{booking.BookingCode}", concierge))
        using (var response = await _fixture.Client.SendAsync(get))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = new
        {
            amount = 10000m,
            method = "BankTransfer",
            externalReference = $"FIN-{Guid.NewGuid():N}",
            idempotencyKey = $"folio-payment-{Guid.NewGuid():N}",
            confirmReservation = false
        };
        using (var post = AuthorizedJson(
                   HttpMethod.Post, $"/api/folios/{booking.BookingCode}/payments", frontDesk, body))
        using (var response = await _fixture.Client.SendAsync(post))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using (var post = AuthorizedJson(
                   HttpMethod.Post, $"/api/folios/{booking.BookingCode}/payments", finance, body))
        using (var response = await _fixture.Client.SendAsync(post))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var folios = scope.ServiceProvider.GetRequiredService<IFolioService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => folios.PostPaymentAsync(
            booking.BookingCode,
            new PostFolioPaymentRequest(
                1000m, "Cash", $"CASH-{Guid.NewGuid():N}", $"cash-{Guid.NewGuid():N}", false),
            frontDesk.Id));
        await Assert.ThrowsAsync<BadRequestException>(() => folios.PostPaymentAsync(
            booking.BookingCode,
            new PostFolioPaymentRequest(
                1000m, "Monnify", $"MNFY-{Guid.NewGuid():N}", $"monnify-{Guid.NewGuid():N}", false),
            _fixture.Admin.Id));
    }

    [Fact]
    public async Task Operational_work_requires_the_correct_department_and_assigned_worker()
    {
        var room = await _fixture.CreateRoomAsync();
        var frontDesk = await _fixture.CreateUserAsync(UserRole.Staff, "FrontDesk");
        var firstHousekeeper = await _fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var secondHousekeeper = await _fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var engineer = await _fixture.CreateUserAsync(UserRole.Staff, "Engineering");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var housekeeping = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        var task = await housekeeping.CreateTaskAsync(
            new CreateHousekeepingTaskRequest(
                room.Id, null, HousekeepingTaskType.StayoverService, WorkPriority.Normal,
                "Refresh the occupied-room supplies."),
            _fixture.Manager.Id);

        await Assert.ThrowsAsync<BadRequestException>(() => housekeeping.UpdateTaskAsync(
            task.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Assigned, frontDesk.Id, null, null),
            _fixture.Manager.Id));
        await housekeeping.UpdateTaskAsync(
            task.Id,
            new UpdateHousekeepingTaskRequest(
                OperationalTaskStatus.Assigned, firstHousekeeper.Id, null, null),
            _fixture.Manager.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => housekeeping.UpdateTaskAsync(
            task.Id,
            new UpdateHousekeepingTaskRequest(
                OperationalTaskStatus.InProgress, firstHousekeeper.Id, null, null),
            secondHousekeeper.Id));
        var started = await housekeeping.UpdateTaskAsync(
            task.Id,
            new UpdateHousekeepingTaskRequest(
                OperationalTaskStatus.InProgress, firstHousekeeper.Id, null, null),
            firstHousekeeper.Id);
        Assert.Equal(OperationalTaskStatus.InProgress, started.Status);

        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var invalidWorkOrder = new CreateMaintenanceWorkOrderRequest(
            room.Id,
            "Inspect electrical outlet",
            "Inspect and repair the reported electrical outlet fault.",
            WorkPriority.High,
            time.Today.AddDays(2),
            time.Today.AddDays(3),
            frontDesk.Id);
        await Assert.ThrowsAsync<BadRequestException>(() => housekeeping.CreateWorkOrderAsync(
            invalidWorkOrder,
            _fixture.Manager.Id));
        var valid = invalidWorkOrder with { AssignedToUserId = engineer.Id };
        var workOrder = await housekeeping.CreateWorkOrderAsync(valid, _fixture.Manager.Id);
        Assert.Equal(engineer.Id, workOrder.AssignedToUserId);
    }

    [Fact]
    public async Task Historical_inventory_and_receivables_ignore_later_changes()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var reporting = scope.ServiceProvider.GetRequiredService<IOperationalReportingService>();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var historicDate = time.Today.AddDays(-40);
        await SeedHistoricInventoryAsync(historicDate.AddDays(-5), historicDate.AddDays(5));
        var receivable = await _fixture.CreateBookingAsync(
            createdAtUtc: time.GetLocalDayStartUtc(historicDate),
            checkInUtc: DateTime.UtcNow.AddDays(20),
            checkOutUtc: DateTime.UtcNow.AddDays(22));

        var before = await reporting.GetReportAsync(historicDate, historicDate);
        Assert.True(before.AvailableRoomNights >= 1);
        var laterRoom = await _fixture.CreateRoomAsync();
        var rooms = scope.ServiceProvider.GetRequiredService<IRoomService>();
        await Assert.ThrowsAsync<ConflictException>(() =>
            rooms.DeleteRoomAsync(laterRoom.Id, _fixture.Manager.Id));
        var folios = scope.ServiceProvider.GetRequiredService<IFolioService>();
        await folios.PostPaymentAsync(
            receivable.BookingCode,
            new PostFolioPaymentRequest(
                10000m, "Cash", $"LATE-{Guid.NewGuid():N}", $"late-{Guid.NewGuid():N}", false),
            _fixture.Manager.Id);
        var after = await reporting.GetReportAsync(historicDate, historicDate);

        Assert.Equal(before.AvailableRoomNights, after.AvailableRoomNights);
        Assert.Equal(before.Receivables, after.Receivables);
    }

    [Fact]
    public async Task Night_audit_rejects_nonsequential_business_dates()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var reporting = scope.ServiceProvider.GetRequiredService<IOperationalReportingService>();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var existing = await reporting.GetNightAuditsAsync();
        DateOnly invalidDate;
        if (existing.Count == 0)
        {
            var first = time.Today.AddDays(-1);
            await _fixture.WithDbAsync(async db =>
            {
                db.NightAudits.Add(new NightAudit
                {
                    Id = Guid.NewGuid(),
                    BusinessDate = first,
                    SnapshotJson = "{}",
                    ClosedAtUtc = DateTime.UtcNow,
                    ClosedByUserId = _fixture.Manager.Id
                });
                await db.SaveChangesAsync();
                return true;
            });
            invalidDate = first.AddDays(-2);
        }
        else
        {
            invalidDate = existing.Min(item => item.BusinessDate).AddDays(-20);
        }

        await Assert.ThrowsAsync<ConflictException>(() =>
            reporting.CloseNightAuditAsync(invalidDate, _fixture.Manager.Id));
    }

    [Fact]
    public async Task Privacy_completion_requires_identity_and_delivery_evidence()
    {
        await _fixture.LinkGuestProfileAsync(_fixture.ClientUser);
        using (var create = AuthorizedJson(
                   HttpMethod.Post,
                   "/api/privacy/requests",
                   _fixture.ClientUser,
                   new { type = "portability", details = "Provide my portable reservation data." }))
        using (var response = await _fixture.Client.SendAsync(create))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Guid requestId;
        using (var mine = Authorized(HttpMethod.Get, "/api/privacy/requests/mine", _fixture.ClientUser))
        using (var response = await _fixture.Client.SendAsync(mine))
        {
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            requestId = json.RootElement.EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "portability")
                .GetProperty("id").GetGuid();
        }

        using (var incomplete = AuthorizedJson(
                   HttpMethod.Patch,
                   $"/api/privacy/requests/{requestId}/status",
                   _fixture.Admin,
                   new { status = "completed", resolutionNotes = "Attempted without evidence." }))
        using (var response = await _fixture.Client.SendAsync(incomplete))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var complete = AuthorizedJson(
                   HttpMethod.Patch,
                   $"/api/privacy/requests/{requestId}/status",
                   _fixture.Admin,
                   new
                   {
                       status = "completed",
                       resolutionNotes = "Portable export generated and delivered securely.",
                       identityVerificationReference = "IDENTITY-TICKET-PORTABILITY",
                       fulfillmentEvidenceReference = "SECURE-DELIVERY-PORTABILITY"
                   }))
        using (var response = await _fixture.Client.SendAsync(complete))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(64, json.RootElement.GetProperty("fulfillmentDigest").GetString()!.Length);
            Assert.NotEqual(JsonValueKind.Null, json.RootElement.GetProperty("exportGeneratedAtUtc").ValueKind);
        }

        using var export = Authorized(HttpMethod.Get, "/api/privacy/export", _fixture.ClientUser);
        using var exportResponse = await _fixture.Client.SendAsync(export);
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        using var package = JsonDocument.Parse(await exportResponse.Content.ReadAsStringAsync());
        Assert.True(package.RootElement.TryGetProperty("processingInformation", out _));
        Assert.True(package.RootElement.TryGetProperty("guestNotes", out _));
        Assert.True(package.RootElement.TryGetProperty("paymentProviderTransactions", out _));
        Assert.True(package.RootElement.TryGetProperty("auditTrail", out _));
    }

    [Fact]
    public async Task Privacy_rectification_erasure_restriction_and_objection_execute_their_actions()
    {
        var rectification = await SeedPrivacyRequestAsync(DataSubjectRequestType.Rectification, "rectify");
        await CompletePrivacyRequestAsync(
            rectification.RequestId,
            new
            {
                status = "completed",
                resolutionNotes = "The verified spelling correction was applied.",
                identityVerificationReference = "IDENTITY-RECTIFICATION",
                fulfillmentEvidenceReference = "CHANGE-REVIEW-RECTIFICATION",
                rectification = new { firstName = "Corrected" }
            });
        Assert.Equal("Corrected", await _fixture.WithDbAsync(db => db.Guests
            .Where(item => item.Id == rectification.GuestId).Select(item => item.FirstName).SingleAsync()));

        var restriction = await SeedPrivacyRequestAsync(DataSubjectRequestType.Restriction, "restrict");
        await CompletePrivacyRequestAsync(
            restriction.RequestId,
            CompletionBody("IDENTITY-RESTRICTION", "RESTRICTION-APPLIED", true));
        Assert.NotNull(await _fixture.WithDbAsync(db => db.Guests
            .Where(item => item.Id == restriction.GuestId)
            .Select(item => item.ProcessingRestrictedAtUtc).SingleAsync()));

        var objection = await SeedPrivacyRequestAsync(DataSubjectRequestType.Objection, "object");
        await CompletePrivacyRequestAsync(
            objection.RequestId,
            CompletionBody("IDENTITY-OBJECTION", "MARKETING-SUPPRESSION", true));
        var objected = await _fixture.WithDbAsync(db => db.Guests.AsNoTracking()
            .SingleAsync(item => item.Id == objection.GuestId));
        Assert.NotNull(objected.MarketingObjectedAtUtc);
        using (var preferences = JsonDocument.Parse(objected.PreferencesJson))
            Assert.False(preferences.RootElement.GetProperty("marketingOptIn").GetBoolean());

        var erasure = await SeedPrivacyRequestAsync(DataSubjectRequestType.Erasure, "erase");
        var adminOutboxId = Guid.NewGuid();
        await _fixture.WithDbAsync(async db =>
        {
            db.EmailOutboxMessages.Add(new EmailOutboxMessage
            {
                Id = adminOutboxId,
                Template = TransactionalEmailTemplates.AdminNewBooking,
                Recipient = "admin@example.test",
                DataSubjectGuestId = erasure.GuestId,
                ProtectedPayload = "protected-admin-directed-guest-pii",
                NextAttemptAtUtc = DateTime.UtcNow.AddHours(1),
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return true;
        });
        await CompletePrivacyRequestAsync(
            erasure.RequestId,
            CompletionBody("IDENTITY-ERASURE", "ERASURE-REVIEW", true));
        var erased = await _fixture.WithDbAsync(db => db.Guests.AsNoTracking()
            .SingleAsync(item => item.Id == erasure.GuestId));
        Assert.Equal("Former", erased.FirstName);
        Assert.Equal("REDACTED", erased.Phone);
        Assert.NotNull(erased.AnonymizedAtUtc);
        Assert.False(await _fixture.WithDbAsync(db => db.EmailOutboxMessages
            .AnyAsync(item => item.Id == adminOutboxId)));
    }

    private async Task SeedHistoricInventoryAsync(DateOnly fromDate, DateOnly toDate)
    {
        await _fixture.WithDbAsync(async db =>
        {
            var unique = Guid.NewGuid().ToString("N").ToUpperInvariant();
            var type = new RoomType
            {
                Id = Guid.NewGuid(),
                Code = $"HIST-{unique[..8]}",
                Name = "Historic Room Type",
                Category = RoomCategory.Standard,
                BaseOccupancy = 1,
                MaxOccupancy = 2,
                BasePricePerNight = 50000m,
                Description = "Historical reporting test inventory.",
                Amenities = ["Wi-Fi"]
            };
            var room = new Room
            {
                Id = Guid.NewGuid(),
                RoomTypeId = type.Id,
                RoomNumber = $"H-{unique[..8]}",
                Name = "Historic Room",
                Category = RoomCategory.Standard,
                Floor = PropertyFloor.GroundFloor,
                Status = RoomStatus.Available,
                PricePerNight = 50000m,
                Capacity = 2,
                Size = "25 sqm",
                IsOnline = false,
                Description = "Historical reporting test room.",
                Amenities = ["Wi-Fi"],
                CreatedAt = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            };
            room.InventoryPeriods.Add(new RoomInventoryPeriod
            {
                Id = Guid.NewGuid(),
                RoomId = room.Id,
                StartDate = fromDate,
                EndDate = toDate,
                RecordedAtUtc = room.CreatedAt
            });
            db.RoomTypes.Add(type);
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            return true;
        });
    }

    private async Task<(Guid RequestId, string GuestId)> SeedPrivacyRequestAsync(
        DataSubjectRequestType type,
        string suffix)
    {
        return await _fixture.WithDbAsync(async db =>
        {
            var unique = Guid.NewGuid().ToString("N").ToUpperInvariant();
            var guest = new Guest
            {
                Id = $"GS-{unique[..16]}",
                FirstName = "Privacy",
                LastName = "Subject",
                Email = $"privacy-{suffix}-{unique[..8].ToLowerInvariant()}@example.test",
                NormalizedEmail = $"privacy-{suffix}-{unique[..8].ToLowerInvariant()}@example.test",
                Phone = "+2348000000099",
                NormalizedPhone = "+2348000000099",
                PreferencesJson = "{\"marketingOptIn\":true}"
            };
            var now = DateTime.UtcNow;
            var request = new PrivacyRequest
            {
                Id = Guid.NewGuid(),
                GuestId = guest.Id,
                Type = type,
                Status = DataSubjectRequestStatus.Pending,
                Details = $"Test {type} fulfillment.",
                RequestedAtUtc = now,
                DueAtUtc = now.AddDays(30)
            };
            db.Guests.Add(guest);
            db.PrivacyRequests.Add(request);
            await db.SaveChangesAsync();
            return (request.Id, guest.Id);
        });
    }

    private async Task CompletePrivacyRequestAsync(Guid requestId, object body)
    {
        using var request = AuthorizedJson(
            HttpMethod.Patch,
            $"/api/privacy/requests/{requestId}/status",
            _fixture.Admin,
            body);
        using var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static object CompletionBody(string identity, string evidence, bool confirmAction) => new
    {
        status = "completed",
        resolutionNotes = "The verified privacy action was completed.",
        identityVerificationReference = identity,
        fulfillmentEvidenceReference = evidence,
        confirmAction
    };

    private static HttpRequestMessage AuthorizedJson(
        HttpMethod method,
        string path,
        TestUser actor,
        object body)
    {
        var request = Authorized(method, path, actor);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, TestUser actor)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }
}
