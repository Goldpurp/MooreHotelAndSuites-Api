using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using Npgsql;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class AnalyticsAndAddOnSchemaTests
{
    private readonly ManualTransferTestFixture _fixture;

    public AnalyticsAndAddOnSchemaTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Analytics_overview_executes_against_the_scoped_database_context()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/analytics/overview");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _fixture.Admin.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Analytics_uses_payment_dates_and_room_nights()
    {
        var checkIn = new DateTime(2020, 1, 10, 14, 0, 0, DateTimeKind.Utc);
        var checkOut = new DateTime(2020, 1, 12, 12, 0, 0, DateTimeKind.Utc);
        var paymentConfirmed = new DateTime(2020, 1, 11, 10, 0, 0, DateTimeKind.Utc);
        await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.CheckedOut,
            checkInUtc: checkIn,
            checkOutUtc: checkOut,
            paymentConfirmedAtUtc: paymentConfirmed);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var periodStart = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            100000m,
            await repository.GetNetRevenueAsync(periodStart, periodEnd));
        Assert.Equal(
            2,
            await repository.GetOccupiedRoomNightsAsync(periodStart, periodEnd));
        Assert.Equal(
            50000m,
            await repository.GetAverageNightlyRateAsync(periodStart, periodEnd));
    }

    [Fact]
    public async Task Add_on_migration_creates_the_public_catalog_schema()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/addons");
        request.Headers.Add("X-Moore-App-Environment", "local");

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Add_on_price_is_enforced_by_postgres()
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            _fixture.WithDbAsync(async db =>
            {
                db.AddOnServices.Add(new AddOnService
                {
                    Id = Guid.NewGuid(),
                    Name = "Invalid free service",
                    Description = "The database must reject this row.",
                    Price = 0m,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
                return true;
            }));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal("CK_addon_services_price_positive", postgres.ConstraintName);
    }

    [Theory]
    [InlineData(0, 100, 0, "CK_booking_addons_quantity_positive")]
    [InlineData(1, 0, 0, "CK_booking_addons_unit_price_positive")]
    [InlineData(2, 100, 100, "CK_booking_addons_total_matches_quantity")]
    public async Task Booking_add_on_amounts_are_enforced_by_postgres(
        int quantity,
        int unitPrice,
        int totalPrice,
        string expectedConstraint)
    {
        var booking = await _fixture.CreateBookingAsync();
        var service = await _fixture.WithDbAsync(async db =>
        {
            var addOn = new AddOnService
            {
                Id = Guid.NewGuid(),
                Name = $"Constraint test {Guid.NewGuid():N}",
                Description = "Valid catalog service for a constraint test.",
                Price = 100m,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            db.AddOnServices.Add(addOn);
            await db.SaveChangesAsync();
            return addOn;
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            _fixture.WithDbAsync(async db =>
            {
                db.BookingAddOns.Add(new BookingAddOn
                {
                    Id = Guid.NewGuid(),
                    BookingId = booking.Id,
                    AddOnServiceId = service.Id,
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    TotalPrice = totalPrice,
                    AddedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
                return true;
            }));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal(expectedConstraint, postgres.ConstraintName);
    }
}
