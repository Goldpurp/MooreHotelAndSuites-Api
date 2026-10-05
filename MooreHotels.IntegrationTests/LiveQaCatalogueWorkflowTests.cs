using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class LiveQaCatalogueWorkflowTests(ManualTransferTestFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    [Fact]
    public async Task Room_type_create_update_deactivate_keeps_management_history_and_public_visibility_correct()
    {
        var create = new SaveRoomTypeRequest($"QA-{Guid.NewGuid():N}"[..20], "QA category",
            RoomCategory.Standard, 1, 2, 25000m, "Local fixture only", ["Wi-Fi"]);
        using var createdResponse = await Send(HttpMethod.Post, "/api/inventory/room-types", create, true);
        Assert.True(createdResponse.StatusCode == HttpStatusCode.OK, await createdResponse.Content.ReadAsStringAsync());
        var created = (await createdResponse.Content.ReadFromJsonAsync<RoomTypeDto>(Json))!;
        using var publicBefore = await Send(HttpMethod.Get, "/api/inventory/room-types");
        Assert.Contains((await publicBefore.Content.ReadFromJsonAsync<PublicRoomTypeDto[]>(Json))!, t => t.Id == created.Id);
        using var updated = await Send(HttpMethod.Put, $"/api/inventory/room-types/{created.Id}",
            create with { Name = "QA category updated", BasePricePerNight = 26000m, IsActive = false }, true);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var publicAfter = await Send(HttpMethod.Get, "/api/inventory/room-types");
        Assert.DoesNotContain((await publicAfter.Content.ReadFromJsonAsync<PublicRoomTypeDto[]>(Json))!, t => t.Id == created.Id);
        using var managed = await Send(HttpMethod.Get, "/api/inventory/management/room-types", authenticated: true);
        var retained = Assert.Single((await managed.Content.ReadFromJsonAsync<RoomTypeDto[]>(Json))!, t => t.Id == created.Id);
        Assert.False(retained.IsActive);
        Assert.Equal(26000m, retained.BasePricePerNight);
        Assert.Equal("QA category updated", retained.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closure_blocks_only_its_dates_and_deactivation_restores_inventory(bool physicalRoom)
    {
        var room = await fixture.CreateRoomAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
        var start = scope.ServiceProvider.GetRequiredService<IHotelTimeService>().Today.AddDays(30);
        Assert.True((await inventory.GetAvailabilityAsync(room.RoomTypeId, start, start.AddDays(2), 1)).Available);
        var closure = await inventory.CreateClosureAsync(new CreateInventoryClosureRequest(room.RoomTypeId,
            physicalRoom ? room.Id : null, start, start.AddDays(2), 1, "QA inventory closure"), fixture.Manager.Id);
        Assert.False((await inventory.GetAvailabilityAsync(room.RoomTypeId, start, start.AddDays(2), 1)).Available);
        Assert.True((await inventory.GetAvailabilityAsync(room.RoomTypeId, start.AddDays(-1), start, 1)).Available);
        Assert.True((await inventory.GetAvailabilityAsync(room.RoomTypeId, start.AddDays(2), start.AddDays(3), 1)).Available);
        await inventory.DeactivateClosureAsync(closure.Id, fixture.Manager.Id);
        Assert.True((await inventory.GetAvailabilityAsync(room.RoomTypeId, start, start.AddDays(2), 1)).Available);
        Assert.False(await fixture.WithDbAsync(db => db.RoomInventoryClosures
            .Where(c => c.Id == closure.Id).Select(c => c.IsActive).SingleAsync()));
    }

    [Fact]
    public async Task Addon_create_attach_reprice_deactivate_preserves_saved_price_and_folio()
    {
        var booking = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed);
        using var createdResponse = await Send(HttpMethod.Post, "/api/addons",
            new CreateAddOnServiceRequest("QA add-on", "Local fixture only", AddOnCategory.Other, 1000.50m), true);
        Assert.True(createdResponse.StatusCode == HttpStatusCode.Created, await createdResponse.Content.ReadAsStringAsync());
        var addon = (await createdResponse.Content.ReadFromJsonAsync<AddOnServiceDto>(Json))!;
        using var attachedResponse = await Send(HttpMethod.Post, $"/api/addons/bookings/{booking.BookingCode}",
            new AddServiceToBookingRequest(addon.Id, 3, "QA charge"), true);
        Assert.Equal(HttpStatusCode.OK, attachedResponse.StatusCode);
        var attached = (await attachedResponse.Content.ReadFromJsonAsync<BookingAddOnDto>(Json))!;
        Assert.Equal(1000.50m, attached.UnitPrice);
        Assert.Equal(3001.50m, attached.TotalPrice);
        var balance = await fixture.WithDbAsync(async db => FolioAccounting.Calculate(
            await db.FolioEntries.Where(e => e.Folio!.BookingId == booking.Id).ToListAsync()));
        Assert.Equal(3001.50m, balance.AmountDue);
        using var updated = await Send(HttpMethod.Put, $"/api/addons/{addon.Id}",
            new UpdateAddOnServiceRequest("QA repriced", null, null, 2000m, null), true);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var deleted = await Send(HttpMethod.Delete, $"/api/addons/{addon.Id}", authenticated: true);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var hidden = await Send(HttpMethod.Get, $"/api/addons/{addon.Id}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var rejected = await Send(HttpMethod.Post, $"/api/addons/bookings/{booking.BookingCode}",
            new AddServiceToBookingRequest(addon.Id, 1), true);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var saved = await fixture.WithDbAsync(db => db.BookingAddOns.AsNoTracking().SingleAsync(a => a.Id == attached.Id));
        Assert.Equal(1000.50m, saved.UnitPrice);
        Assert.Equal(3001.50m, saved.TotalPrice);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.BookingAddOns.CountAsync(a => a.BookingId == booking.Id)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Invalid_addon_quantity_cannot_change_booking_or_folio(int quantity)
    {
        var booking = await fixture.CreateBookingAsync();
        using var response = await Send(HttpMethod.Post, $"/api/addons/bookings/{booking.BookingCode}",
            new AddServiceToBookingRequest(Guid.NewGuid(), quantity), true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(booking.Amount, await fixture.WithDbAsync(db => db.Bookings
            .Where(b => b.Id == booking.Id).Select(b => b.Amount).SingleAsync()));
        Assert.False(await fixture.WithDbAsync(db => db.BookingAddOns.AnyAsync(a => a.BookingId == booking.Id)));
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("Phone")]
    public async Task Crm_preferences_notes_and_contact_evidence_preserve_access_boundaries(string contactType)
    {
        var booking = await fixture.CreateBookingAsync();
        var path = $"/api/guest-crm/{booking.GuestId}";
        var preferences = new UpdateGuestPreferencesRequest("en", "Twin", "QA dietary preference",
            "QA access preference", true);
        using var noConsent = await Send(HttpMethod.Put, path + "/preferences", preferences, true);
        Assert.Equal(HttpStatusCode.BadRequest, noConsent.StatusCode);
        using var saved = await Send(HttpMethod.Put, path + "/preferences",
            preferences with { MarketingConsentReference = "QA-CONSENT-20261004" }, true);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var note = await Send(HttpMethod.Post, path + "/notes",
            new AddGuestNoteRequest("QA restricted synthetic note", true), true);
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        using var verified = await Send(HttpMethod.Post, path + "/verify-contact",
            new VerifyGuestContactRequest(contactType, "QA-CONTACT-20261004"), true);
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
        using var management = await Send(HttpMethod.Get, path, authenticated: true);
        var profile = (await management.Content.ReadFromJsonAsync<GuestCrmProfileDto>())!;
        Assert.True(profile.Preferences.MarketingOptIn);
        Assert.Equal("Twin", profile.Preferences.BeddingPreference);
        Assert.Single(profile.Notes);
        Assert.NotNull(contactType == "Email" ? profile.EmailVerifiedAtUtc : profile.PhoneVerifiedAtUtc);
        var reception = await fixture.CreateUserAsync(UserRole.Staff, "Reception");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", reception.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        using var limited = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, limited.StatusCode);
        var limitedProfile = (await limited.Content.ReadFromJsonAsync<GuestCrmProfileDto>())!;
        Assert.Empty(limitedProfile.Notes);
        var auditPayloads = await fixture.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityId == booking.GuestId).Select(a => a.NewDataJson).ToListAsync());
        Assert.DoesNotContain(auditPayloads, payload => payload?.Contains("QA restricted synthetic note") == true);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? payload = null, bool authenticated = false)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Moore-App-Environment", "local");
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Admin.Token);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: Json);
        return await fixture.Client.SendAsync(request);
    }
}
