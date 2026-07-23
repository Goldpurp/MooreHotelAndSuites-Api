using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MooreHotels.Domain.Enums;
using MooreHotels.WebAPI.Services;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class BookingEmailLifecycleTests
{
    private readonly ManualTransferTestFixture _fixture;

    public BookingEmailLifecycleTests(ManualTransferTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task New_booking_sends_guest_confirmation_and_admin_alert()
    {
        var room = await _fixture.CreateRoomAsync();
        _fixture.Email.Reset();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/bookings")
        {
            Content = JsonContent.Create(new
            {
                roomId = room.Id,
                guestFirstName = "Email",
                guestLastName = "Lifecycle",
                guestEmail = $"lifecycle-{Guid.NewGuid():N}@example.test",
                guestPhone = "+2348000000002",
                checkIn = DateTime.UtcNow.Date.AddDays(10),
                checkOut = DateTime.UtcNow.Date.AddDays(12),
                paymentMethod = "directTransfer",
                notes = "Email lifecycle integration test"
            })
        };
        AddEnvironmentHeader(request);

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var bookingCode = document.RootElement
            .GetProperty("bookingCode")
            .GetString();
        Assert.Contains(
            _fixture.Email.Messages,
            email => email.Template == "BookingConfirmation" &&
                     email.BookingCode == bookingCode);
        Assert.Contains(
            _fixture.Email.Messages,
            email => email.Template == "AdminNewBooking" &&
                     email.BookingCode == bookingCode &&
                     email.Recipient == "admin-notifications@example.test");
        Assert.Equal(
            1,
            await _fixture.WithDbAsync(db => db.Notifications.CountAsync(
                notification => notification.BookingCode == bookingCode)));
    }

    [Fact]
    public async Task Staff_cancellation_sends_guest_cancellation_notice()
    {
        var booking = await _fixture.CreateBookingAsync();
        _fixture.Email.Reset();
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            $"/api/bookings/{booking.Id}/cancel?reason=Hotel%20maintenance",
            _fixture.Staff);

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _fixture.Email.Messages,
            email => email.Template == "Cancellation" &&
                     email.BookingCode == booking.BookingCode);
    }

    [Fact]
    public async Task Checkout_sends_thank_you_after_state_is_persisted()
    {
        var booking = await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.CheckedIn);
        _fixture.Email.Reset();
        using var request = AuthorizedRequest(
            HttpMethod.Put,
            $"/api/bookings/{booking.Id}/status?status=CheckedOut",
            _fixture.Staff);

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.Id == booking.Id));
        Assert.Equal(BookingStatus.CheckedOut, stored.Status);
        Assert.Contains(
            _fixture.Email.Messages,
            email => email.Template == "CheckOutThankYou" &&
                     email.BookingCode == booking.BookingCode);
    }

    [Fact]
    public async Task No_show_sends_guest_notice()
    {
        var booking = await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed);
        await _fixture.WithDbAsync(async db =>
        {
            var stored = await db.Bookings.SingleAsync(item => item.Id == booking.Id);
            stored.CheckIn = DateTime.UtcNow.AddHours(-2);
            stored.CheckOut = DateTime.UtcNow.AddDays(1);
            await db.SaveChangesAsync();
            return true;
        });
        _fixture.Email.Reset();
        using var request = AuthorizedRequest(
            HttpMethod.Put,
            $"/api/bookings/{booking.Id}/status?status=NoShow",
            _fixture.Manager);

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            _fixture.Email.Messages,
            email => email.Template == "Cancellation" &&
                     email.BookingCode == booking.BookingCode);
    }

    [Fact]
    public async Task One_hour_payment_expiry_cancels_and_emails_guest()
    {
        var booking = await _fixture.CreateBookingAsync(
            createdAtUtc: DateTime.UtcNow.AddMinutes(-61));
        _fixture.Email.Reset();
        var worker = _fixture.Services
            .GetServices<IHostedService>()
            .OfType<PendingBookingExpirationWorker>()
            .Single();

        var expired = await worker.SweepOnceAsync();

        Assert.Equal(1, expired);
        var stored = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.Id == booking.Id));
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Contains(
            _fixture.Email.Messages,
            email => email.Template == "Cancellation" &&
                     email.BookingCode == booking.BookingCode);
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string uri,
        TestUser actor)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", actor.Token);
        AddEnvironmentHeader(request);
        return request;
    }

    private static void AddEnvironmentHeader(HttpRequestMessage request) =>
        request.Headers.Add("X-Moore-App-Environment", "local");
}
