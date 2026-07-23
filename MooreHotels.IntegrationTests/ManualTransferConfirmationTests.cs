using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ManualTransferCollection : ICollectionFixture<ManualTransferTestFixture>
{
    public const string Name = "ManualTransfer";
}

[Collection(ManualTransferCollection.Name)]
public sealed class ManualTransferConfirmationTests
{
    private readonly ManualTransferTestFixture _fixture;

    public ManualTransferConfirmationTests(ManualTransferTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Manager")]
    public async Task Correct_accept_confirms_direct_transfer_for_privileged_roles(string role)
    {
        _fixture.Email.Reset();
        var booking = await _fixture.CreateBookingAsync();
        var actor = role == "Admin" ? _fixture.Admin : _fixture.Manager;

        var response = await ConfirmAsync(
            booking.BookingCode,
            actor,
            "ACCEPT",
            "MANUAL-CLIENT-SUPPLIED-LEGACY-UUID");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ManualTransferConfirmationResponse>();
        Assert.NotNull(body);
        Assert.Equal("Bank transfer payment confirmed manually.", body.Message);
        Assert.Equal(booking.BookingCode, body.Data.BookingCode);
        Assert.Equal("Paid", body.Data.PaymentStatus);
        Assert.Equal("Confirmed", body.Data.Status);
        Assert.Equal("TypedAcknowledgement", body.Data.ConfirmationMethod);
        Assert.StartsWith($"MANUAL-{booking.BookingCode}-", body.Data.TransactionReference);
        Assert.DoesNotContain("CLIENT-SUPPLIED", body.Data.TransactionReference);
        Assert.Equal(DateTimeKind.Utc, body.Data.ConfirmedAtUtc.Kind);

        var stored = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.Id == booking.Id));
        Assert.Equal(PaymentStatus.Paid, stored.PaymentStatus);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Equal(body.Data.TransactionReference, stored.TransactionReference);
        Assert.Equal(ManualTransferConfirmation.Method, stored.PaymentConfirmationMethod);
        Assert.Equal(actor.Id, stored.PaymentConfirmedByUserId);
        Assert.NotNull(stored.PaymentConfirmedAtUtc);
        Assert.Contains(
            _fixture.Email.Messages,
            email => email.Template == "PaymentSuccess" &&
                     email.BookingCode == booking.BookingCode);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData(" ACCEPT")]
    [InlineData("ACCEPT ")]
    public async Task Confirmation_text_is_exact_case_sensitive_and_not_trimmed(string confirmationText)
    {
        var booking = await _fixture.CreateBookingAsync();

        var response = await ConfirmAsync(booking.BookingCode, _fixture.Admin, confirmationText);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("exactly 'ACCEPT'", await response.Content.ReadAsStringAsync());
        await AssertBookingUnchangedAsync(booking.Id);
    }

    [Fact]
    public async Task Missing_confirmation_text_is_rejected()
    {
        var booking = await _fixture.CreateBookingAsync();
        using var request = AuthorizedRequest(
            booking.BookingCode,
            _fixture.Admin,
            new
            {
                confirmationMethod = "TypedAcknowledgement",
                transactionReference = "LEGACY-IGNORED"
            });

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("confirmationText", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ACCEPT", content);
        await AssertBookingUnchangedAsync(booking.Id);
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("Client")]
    public async Task Staff_and_client_accounts_are_forbidden(string role)
    {
        var booking = await _fixture.CreateBookingAsync();
        var actor = role == "Staff" ? _fixture.Staff : _fixture.ClientUser;

        var response = await ConfirmAsync(booking.BookingCode, actor, "ACCEPT");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertBookingUnchangedAsync(booking.Id);
    }

    [Fact]
    public async Task Non_direct_transfer_booking_is_rejected()
    {
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.Monnify,
            PaymentStatus.AwaitingVerification);

        var response = await ConfirmAsync(booking.BookingCode, _fixture.Admin, "ACCEPT");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("DirectTransfer", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Already_paid_booking_is_rejected()
    {
        var booking = await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed);

        var response = await ConfirmAsync(booking.BookingCode, _fixture.Admin, "ACCEPT");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("already been paid", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(PaymentStatus.AwaitingVerification, BookingStatus.Cancelled)]
    [InlineData(PaymentStatus.RefundPending, BookingStatus.Cancelled)]
    [InlineData(PaymentStatus.Refunded, BookingStatus.Cancelled)]
    public async Task Cancelled_or_refunded_booking_is_rejected(
        PaymentStatus paymentStatus,
        BookingStatus bookingStatus)
    {
        var booking = await _fixture.CreateBookingAsync(
            paymentStatus: paymentStatus,
            bookingStatus: bookingStatus);

        var response = await ConfirmAsync(booking.BookingCode, _fixture.Manager, "ACCEPT");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.Id == booking.Id));
        Assert.Equal(paymentStatus, stored.PaymentStatus);
        Assert.Equal(bookingStatus, stored.Status);
    }

    [Fact]
    public async Task Concurrent_duplicate_requests_commit_only_once()
    {
        var booking = await _fixture.CreateBookingAsync();

        var responses = await Task.WhenAll(
            ConfirmAsync(booking.BookingCode, _fixture.Admin, "ACCEPT"),
            ConfirmAsync(booking.BookingCode, _fixture.Admin, "ACCEPT"));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
        var auditCount = await _fixture.WithDbAsync(db => db.AuditLogs.CountAsync(log =>
            log.Action == "MANUAL_PAYMENT_CONFIRMED" &&
            log.EntityId == booking.Id.ToString()));
        Assert.Equal(1, auditCount);
    }

    [Fact]
    public async Task Server_generated_references_are_unique()
    {
        var first = await _fixture.CreateBookingAsync();
        var second = await _fixture.CreateBookingAsync();

        var firstResponse = await ConfirmAsync(first.BookingCode, _fixture.Admin, "ACCEPT");
        var secondResponse = await ConfirmAsync(second.BookingCode, _fixture.Admin, "ACCEPT");
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<ManualTransferConfirmationResponse>();
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<ManualTransferConfirmationResponse>();

        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);
        Assert.NotEqual(firstBody.Data.TransactionReference, secondBody.Data.TransactionReference);
        Assert.StartsWith($"MANUAL-{first.BookingCode}-", firstBody.Data.TransactionReference);
        Assert.StartsWith($"MANUAL-{second.BookingCode}-", secondBody.Data.TransactionReference);
    }

    [Fact]
    public async Task Audit_record_contains_required_non_secret_context()
    {
        var booking = await _fixture.CreateBookingAsync();

        var response = await ConfirmAsync(booking.BookingCode, _fixture.Manager, "ACCEPT");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audit = await _fixture.WithDbAsync(db => db.AuditLogs
            .AsNoTracking()
            .SingleAsync(log => log.Action == "MANUAL_PAYMENT_CONFIRMED" &&
                                log.EntityId == booking.Id.ToString()));
        using var document = JsonDocument.Parse(audit.NewDataJson!);
        var data = document.RootElement;
        Assert.Equal(booking.Id, data.GetProperty("BookingId").GetGuid());
        Assert.Equal(booking.BookingCode, data.GetProperty("BookingCode").GetString());
        Assert.Equal(booking.GuestId, data.GetProperty("GuestId").GetString());
        Assert.Equal(booking.Amount, data.GetProperty("AmountConfirmed").GetDecimal());
        Assert.Equal("AwaitingVerification", data.GetProperty("PreviousPaymentStatus").GetString());
        Assert.Equal("Paid", data.GetProperty("NewPaymentStatus").GetString());
        Assert.StartsWith("MANUAL-", data.GetProperty("InternalConfirmationReference").GetString());
        Assert.Equal(_fixture.Manager.Id, data.GetProperty("ConfirmingStaffId").GetGuid());
        Assert.Equal("Integration Manager", data.GetProperty("ConfirmingStaffName").GetString());
        Assert.Equal("Manager", data.GetProperty("ConfirmingStaffRole").GetString());
        Assert.Equal("TypedAcknowledgement", data.GetProperty("ConfirmationMethod").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("RequestId").GetString()));
        Assert.DoesNotContain("jwt", audit.NewDataJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", audit.NewDataJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CLIENT-SUPPLIED", audit.NewDataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Booking_update_rolls_back_when_audit_insert_fails()
    {
        var booking = await _fixture.CreateBookingAsync();
        await _fixture.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION fail_manual_transfer_audit() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'forced audit failure';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_manual_transfer_audit_trigger
                BEFORE INSERT ON audit_logs
                FOR EACH ROW EXECUTE FUNCTION fail_manual_transfer_audit();
                """);
            return true;
        });

        try
        {
            var response = await ConfirmAsync(booking.BookingCode, _fixture.Admin, "ACCEPT");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await AssertBookingUnchangedAsync(booking.Id);
            var auditCount = await _fixture.WithDbAsync(db => db.AuditLogs.CountAsync(log =>
                log.EntityId == booking.Id.ToString()));
            Assert.Equal(0, auditCount);
        }
        finally
        {
            await _fixture.WithDbAsync(async db =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DROP TRIGGER IF EXISTS fail_manual_transfer_audit_trigger ON audit_logs;
                    DROP FUNCTION IF EXISTS fail_manual_transfer_audit();
                    """);
                return true;
            });
        }
    }

    [Fact]
    public async Task Swagger_exposes_contract_without_ui_descriptions()
    {
        var document = await _fixture.Client.GetStringAsync("/swagger/v1/swagger.json");

        Assert.Contains("/api/bookings/{bookingCode}/confirm-transfer", document);
        Assert.Contains("confirmationText", document);

        using var json = JsonDocument.Parse(document);
        AssertNoVisibleSwaggerDescriptions(json.RootElement);
    }

    private static void AssertNoVisibleSwaggerDescriptions(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if ((property.NameEquals("description") ||
                     property.NameEquals("summary")) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    Assert.True(
                        string.IsNullOrEmpty(property.Value.GetString()),
                        $"Swagger contains a visible {property.Name}: {property.Value.GetString()}");
                }

                AssertNoVisibleSwaggerDescriptions(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertNoVisibleSwaggerDescriptions(item);
            }
        }
    }

    private async Task<HttpResponseMessage> ConfirmAsync(
        string bookingCode,
        TestUser actor,
        string confirmationText,
        string? legacyReference = "MANUAL-CLIENT-LEGACY-UUID")
    {
        using var request = AuthorizedRequest(
            bookingCode,
            actor,
            new
            {
                confirmationText,
                confirmationMethod = "TypedAcknowledgement",
                transactionReference = legacyReference
            });
        return await _fixture.Client.SendAsync(request);
    }

    private static HttpRequestMessage AuthorizedRequest(
        string bookingCode,
        TestUser actor,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/bookings/{bookingCode}/confirm-transfer")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }

    private async Task AssertBookingUnchangedAsync(Guid bookingId)
    {
        var stored = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.Id == bookingId));
        Assert.Equal(PaymentStatus.AwaitingVerification, stored.PaymentStatus);
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Null(stored.TransactionReference);
        Assert.Null(stored.PaymentConfirmationMethod);
        Assert.Null(stored.PaymentConfirmedByUserId);
        Assert.Null(stored.PaymentConfirmedAtUtc);
    }
}
