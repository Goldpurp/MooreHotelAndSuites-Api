using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Domain.Entities;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class WorkerRecoverySecurityTests
{
    private readonly ManualTransferTestFixture _fixture;

    public WorkerRecoverySecurityTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Booking_repository_rejects_a_guest_anonymized_before_commit()
    {
        var guestId = $"GS-{Guid.NewGuid():N}"[..19].ToUpperInvariant();
        await _fixture.WithDbAsync(async db =>
        {
            db.Guests.Add(new Guest
            {
                Id = guestId,
                FirstName = "Former",
                LastName = "Guest",
                Email = $"anonymized-{guestId.ToLowerInvariant()}@privacy.invalid",
                NormalizedEmail = $"anonymized-{guestId.ToLowerInvariant()}@privacy.invalid",
                Phone = "REDACTED",
                NormalizedPhone = string.Empty,
                AnonymizedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return true;
        });

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var staleBooking = new Booking
        {
            Id = Guid.NewGuid(),
            GuestId = guestId,
            RoomTypeId = Guid.NewGuid()
        };

        await Assert.ThrowsAsync<BadRequestException>(() =>
            repository.AddAsync(staleBooking));
    }

    [Fact]
    public async Task Privacy_scrubbed_email_dead_letter_cannot_be_replayed()
    {
        var messageId = Guid.NewGuid();
        await _fixture.WithDbAsync(async db =>
        {
            db.EmailOutboxMessages.Add(new EmailOutboxMessage
            {
                Id = messageId,
                Template = "BookingConfirmation",
                Recipient = "quarantined-failure@delivery-failure.invalid",
                ProtectedPayload = "{}",
                AttemptCount = 12,
                QuarantinedAtUtc = DateTime.UtcNow,
                NextAttemptAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return true;
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/operations/email-outbox/{messageId}/retry");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _fixture.Admin.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await _fixture.WithDbAsync(db =>
            db.EmailOutboxMessages.AsNoTracking().SingleAsync(item => item.Id == messageId));
        Assert.Equal(12, stored.AttemptCount);
        Assert.NotNull(stored.QuarantinedAtUtc);
    }
}
