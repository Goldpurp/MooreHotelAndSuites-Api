using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class PaymentReadinessTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData("Finance")]
    [InlineData("Cashier")]
    public async Task Finance_can_page_payment_records_without_reservation_PII_access(string department)
    {
        var actor = await fixture.CreateUserAsync(UserRole.Staff, department);
        for (var i = 0; i < 21; i++) await fixture.CreateBookingAsync();
        using var response = await Send(actor, HttpMethod.Get, "/api/folios?page=2&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("items").GetArrayLength() > 0);
        Assert.True(json.RootElement.GetProperty("totalCount").GetInt32() > 20);
        foreach (var forbidden in new[] { "guestEmail", "guestPhone", "guestAccessToken", "notes" })
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        using var reservations = await Send(actor, HttpMethod.Get, "/api/bookings");
        Assert.Equal(HttpStatusCode.Forbidden, reservations.StatusCode);
    }

    [Fact]
    public async Task Housekeeping_cannot_read_payment_records()
    {
        var actor = await fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        using var response = await Send(actor, HttpMethod.Get, "/api/folios");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null, 35000, "Checked hotel bank statement")]
    [InlineData("MANUAL-INTERNAL-REFERENCE", 35000, "Checked hotel bank statement")]
    [InlineData("BANK1234", 1, "Checked hotel bank statement")]
    [InlineData("BANK1234", 35000, "short")]
    public async Task Ordinary_confirmation_requires_real_evidence_fields(string? reference, decimal amount, string reason)
    {
        var booking = await fixture.CreateBookingAsync();
        using var response = await Send(fixture.Admin, HttpMethod.Post,
            $"/api/bookings/{booking.BookingCode}/confirm-transfer",
            new { confirmationText = "ACCEPT", bankReference = reference, amount, reason });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PaymentStatus.AwaitingVerification, await fixture.WithDbAsync(db =>
            db.Bookings.Where(b => b.Id == booking.Id).Select(b => b.PaymentStatus).SingleAsync()));
    }

    [Fact]
    public async Task Concurrent_bank_reference_reuse_across_bookings_is_rejected()
    {
        var first = await fixture.CreateBookingAsync();
        var second = await fixture.CreateBookingAsync();
        var reference = "BANK-" + Guid.NewGuid().ToString("N");
        var responses = await Task.WhenAll(new[] { first, second }.Select(b => Send(fixture.Admin,
            HttpMethod.Post, $"/api/bookings/{b.BookingCode}/confirm-transfer",
            new { confirmationText = "ACCEPT", bankReference = reference, amount = b.Amount, reason = "Matched hotel bank statement credit" })));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.FolioEntries.CountAsync(e => e.ExternalReference == reference.ToUpperInvariant())));
        foreach (var response in responses) response.Dispose();
    }

    private async Task<HttpResponseMessage> Send(TestUser actor, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        if (body is not null) request.Content = JsonContent.Create(body);
        return await fixture.Client.SendAsync(request);
    }
}
