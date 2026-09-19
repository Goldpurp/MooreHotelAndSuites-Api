using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.DTOs.Pricing;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.WebAPI.Services;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class ProductionHardeningSixToEightTests
{
    private readonly ManualTransferTestFixture _fixture;

    public ProductionHardeningSixToEightTests(ManualTransferTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Retention_sweep_scrubs_booking_and_addon_notes_and_quarantines_exhausted_emails_while_respecting_legal_hold()
    {
        var (heldGuest, heldBooking) = await CreateGuestWithStayAsync(
            isUnderLegalHold: true,
            yearsAgo: 8,
            notes: "Sensitive VIP booking notes",
            refundNotes: "Sensitive refund notes",
            addOnNotes: "Sensitive add-on notes");

        var (normalGuest, normalBooking) = await CreateGuestWithStayAsync(
            isUnderLegalHold: false,
            yearsAgo: 8,
            notes: "Standard past notes",
            refundNotes: "Standard refund notes",
            addOnNotes: "Standard add-on notes");

        var exhaustedEmailId = Guid.NewGuid();
        var retainedHeldEmailId = Guid.NewGuid();
        var removedExpiredEmailId = Guid.NewGuid();
        var retainedHeldVerificationId = Guid.NewGuid();
        var removedExpiredVerificationId = Guid.NewGuid();
        await _fixture.WithDbAsync(async db =>
        {
            db.EmailOutboxMessages.AddRange(
                new EmailOutboxMessage
                {
                    Id = exhaustedEmailId,
                    Template = TransactionalEmailTemplates.BookingConfirmation,
                    Recipient = "exhausted@delivery-test.invalid",
                    ProtectedPayload = "{\"secret\":\"guest-pii-data\"}",
                    AttemptCount = 12,
                    NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(-10),
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-2)
                },
                new EmailOutboxMessage
                {
                    Id = retainedHeldEmailId,
                    Template = TransactionalEmailTemplates.AdminNewBooking,
                    Recipient = "admin@example.test",
                    DataSubjectGuestId = heldGuest.Id,
                    ProtectedPayload = "protected-held-guest-pii",
                    NextAttemptAtUtc = DateTime.UtcNow.AddHours(1),
                    CreatedAtUtc = DateTime.UtcNow
                },
                new EmailOutboxMessage
                {
                    Id = removedExpiredEmailId,
                    Template = TransactionalEmailTemplates.AdminNewBooking,
                    Recipient = "admin@example.test",
                    DataSubjectGuestId = normalGuest.Id,
                    ProtectedPayload = "protected-expired-guest-pii",
                    NextAttemptAtUtc = DateTime.UtcNow.AddHours(1),
                    CreatedAtUtc = DateTime.UtcNow
                });
            db.BookingEmailVerifications.AddRange(
                new BookingEmailVerification
                {
                    Id = retainedHeldVerificationId,
                    Email = heldGuest.Email,
                    TokenHash = BookingGuestAccess.Hash(Guid.NewGuid().ToString("N")),
                    CreatedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
                },
                new BookingEmailVerification
                {
                    Id = removedExpiredVerificationId,
                    Email = normalGuest.Email,
                    TokenHash = BookingGuestAccess.Hash(Guid.NewGuid().ToString("N")),
                    CreatedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
                });
            await db.SaveChangesAsync();
            return true;
        });

        var worker = new PrivacyRetentionWorker(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PrivacySettings
            {
                EnableRetentionWorker = true,
                GuestRetentionDays = 2555
            }),
            _fixture.Services.GetRequiredService<ILogger<PrivacyRetentionWorker>>());
        await worker.SweepOnceAsync();

        await _fixture.WithDbAsync(async db =>
        {
            // 1. Guest under legal hold: NOT anonymized, booking and add-on notes preserved
            var g1 = await db.Guests.SingleAsync(g => g.Id == heldGuest.Id);
            Assert.Null(g1.AnonymizedAtUtc);
            Assert.True(g1.IsUnderLegalHold);

            var b1 = await db.Bookings.SingleAsync(b => b.Id == heldBooking.Id);
            Assert.Equal("Sensitive VIP booking notes", b1.Notes);
            Assert.Equal("Sensitive refund notes", b1.RefundNotes);

            var a1 = await db.BookingAddOns.SingleAsync(a => a.BookingId == heldBooking.Id);
            Assert.Equal("Sensitive add-on notes", a1.Notes);

            // 2. Normal guest past retention period: anonymized, notes and tokens scrubbed
            var g2 = await db.Guests.SingleAsync(g => g.Id == normalGuest.Id);
            Assert.NotNull(g2.AnonymizedAtUtc);
            Assert.StartsWith("anonymized-", g2.Email);

            var b2 = await db.Bookings.SingleAsync(b => b.Id == normalBooking.Id);
            Assert.Null(b2.Notes);
            Assert.Null(b2.RefundNotes);
            Assert.Null(b2.GuestAccessTokenHash);

            var a2 = await db.BookingAddOns.SingleAsync(a => a.BookingId == normalBooking.Id);
            Assert.Null(a2.Notes);

            // 3. Exhausted email: quarantined, recipient & payload scrubbed, failure metadata retained
            var email = await db.EmailOutboxMessages.SingleAsync(e => e.Id == exhaustedEmailId);
            Assert.NotNull(email.QuarantinedAtUtc);
            Assert.Equal("redacted@delivery-failure.invalid", email.Recipient);
            Assert.Equal("{}", email.ProtectedPayload);
            Assert.NotNull(email.DeliveryFailureMetadataJson);
            Assert.Contains("retention_sweep", email.DeliveryFailureMetadataJson);
            Assert.True(await db.EmailOutboxMessages.AnyAsync(e => e.Id == retainedHeldEmailId));
            Assert.False(await db.EmailOutboxMessages.AnyAsync(e => e.Id == removedExpiredEmailId));
            Assert.True(await db.BookingEmailVerifications.AnyAsync(
                verification => verification.Id == retainedHeldVerificationId));
            Assert.False(await db.BookingEmailVerifications.AnyAsync(
                verification => verification.Id == removedExpiredVerificationId));

            return true;
        });
    }

    [Fact]
    public async Task Retention_sweep_anonymizes_dormant_linked_client_and_identity_credentials_but_keeps_recent_account()
    {
        var (dormantGuest, _) = await CreateGuestWithStayAsync(
            isUnderLegalHold: false,
            yearsAgo: 8,
            notes: "Dormant account notes",
            refundNotes: "Dormant account refund notes",
            addOnNotes: "Dormant account add-on notes");
        var (recentGuest, _) = await CreateGuestWithStayAsync(
            isUnderLegalHold: false,
            yearsAgo: 8,
            notes: "Recent account notes",
            refundNotes: "Recent account refund notes",
            addOnNotes: "Recent account add-on notes");
        var dormantClient = await _fixture.CreateUserAsync(UserRole.Client);
        var recentClient = await _fixture.CreateUserAsync(UserRole.Client);
        var dormantOutboxId = Guid.NewGuid();

        await _fixture.WithDbAsync(async db =>
        {
            var oldActivity = DateTime.UtcNow.AddYears(-8);
            var dormant = await db.Users.SingleAsync(user => user.Id == dormantClient.Id);
            dormant.GuestId = dormantGuest.Id;
            dormant.CreatedAt = oldActivity;
            dormant.LastAuthenticatedAtUtc = oldActivity;
            dormant.StatusChangedAtUtc = oldActivity;
            dormant.AvatarUrl = "/uploads/retention/avatar.png";
            dormant.AvatarPublicId = $"local/retention/{dormant.Id:N}.png";

            var recent = await db.Users.SingleAsync(user => user.Id == recentClient.Id);
            recent.GuestId = recentGuest.Id;
            recent.CreatedAt = oldActivity;
            recent.LastAuthenticatedAtUtc = DateTime.UtcNow.AddDays(-1);
            recent.StatusChangedAtUtc = oldActivity;

            db.UserTokens.Add(new IdentityUserToken<Guid>
            {
                UserId = dormant.Id,
                LoginProvider = "RetentionTest",
                Name = "refresh-token",
                Value = "identity-token-secret"
            });
            db.UserLogins.Add(new IdentityUserLogin<Guid>
            {
                UserId = dormant.Id,
                LoginProvider = "RetentionTest",
                ProviderKey = $"provider-{dormant.Id:N}",
                ProviderDisplayName = "Retention test login"
            });
            db.UserClaims.Add(new IdentityUserClaim<Guid>
            {
                UserId = dormant.Id,
                ClaimType = "guest-email",
                ClaimValue = dormant.Email
            });
            db.EmailOutboxMessages.Add(new EmailOutboxMessage
            {
                Id = dormantOutboxId,
                Template = TransactionalEmailTemplates.BookingConfirmation,
                Recipient = dormant.Email!,
                DataSubjectGuestId = dormantGuest.Id,
                ProtectedPayload = "protected-dormant-account-pii",
                NextAttemptAtUtc = DateTime.UtcNow.AddHours(1),
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return true;
        });

        var worker = new PrivacyRetentionWorker(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PrivacySettings
            {
                EnableRetentionWorker = true,
                GuestRetentionDays = 2555,
                InactiveAccountRetentionDays = 2555
            }),
            _fixture.Services.GetRequiredService<ILogger<PrivacyRetentionWorker>>());

        Assert.True(await worker.SweepOnceAsync() >= 1);
        await _fixture.WithDbAsync(async db =>
        {
            var dormantGuestAfter = await db.Guests.AsNoTracking()
                .SingleAsync(guest => guest.Id == dormantGuest.Id);
            var dormantAfter = await db.Users.AsNoTracking()
                .SingleAsync(user => user.Id == dormantClient.Id);
            Assert.NotNull(dormantGuestAfter.AnonymizedAtUtc);
            Assert.NotNull(dormantAfter.AnonymizedAtUtc);
            Assert.Equal(ProfileStatus.Suspended, dormantAfter.Status);
            Assert.Equal("Former Guest", dormantAfter.Name);
            Assert.EndsWith("@privacy.invalid", dormantAfter.Email);
            Assert.Null(dormantAfter.PasswordHash);
            Assert.Null(dormantAfter.LastAuthenticatedAtUtc);
            Assert.Null(dormantAfter.AvatarPublicId);
            Assert.False(await db.UserTokens.AnyAsync(token => token.UserId == dormantClient.Id));
            Assert.False(await db.UserLogins.AnyAsync(login => login.UserId == dormantClient.Id));
            Assert.False(await db.UserClaims.AnyAsync(claim => claim.UserId == dormantClient.Id));
            Assert.False(await db.EmailOutboxMessages.AnyAsync(message => message.Id == dormantOutboxId));
            Assert.True(await db.MediaDeletionJobs.AnyAsync(job =>
                job.SourceType == "AccountRetention" && job.SourceId == dormantGuest.Id));

            var recentGuestAfter = await db.Guests.AsNoTracking()
                .SingleAsync(guest => guest.Id == recentGuest.Id);
            var recentAfter = await db.Users.AsNoTracking()
                .SingleAsync(user => user.Id == recentClient.Id);
            Assert.Null(recentGuestAfter.AnonymizedAtUtc);
            Assert.Null(recentAfter.AnonymizedAtUtc);
            Assert.NotNull(recentAfter.PasswordHash);
            return true;
        });
    }

    [Fact]
    public async Task Audit_payloads_are_sanitized_at_the_persistence_boundary()
    {
        var auditId = Guid.NewGuid();
        await _fixture.WithDbAsync(async db =>
        {
            db.AuditLogs.Add(new AuditLog
            {
                Id = auditId,
                ProfileId = _fixture.Admin.Id,
                Action = "AUDIT_SANITIZER_TEST",
                EntityType = "Booking",
                EntityId = "MH-TEST-001",
                OldDataJson = "{\"Status\":\"Pending\",\"GuestEmail\":\"old@example.test\"}",
                NewDataJson = "{\"Status\":\"Confirmed\",\"Reason\":\"private incident\",\"AccessToken\":\"secret\"}",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return true;
        });

        var audit = await _fixture.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .SingleAsync(log => log.Id == auditId));
        Assert.Contains("Pending", audit.OldDataJson);
        Assert.Contains("Confirmed", audit.NewDataJson);
        Assert.DoesNotContain("old@example.test", audit.OldDataJson);
        Assert.DoesNotContain("private incident", audit.NewDataJson);
        Assert.DoesNotContain("secret", audit.NewDataJson);
        Assert.Contains(AuditDataSanitizer.RedactedValue, audit.OldDataJson);
        Assert.Contains(AuditDataSanitizer.RedactedValue, audit.NewDataJson);
    }

    [Fact]
    public async Task Legal_hold_prevents_erasure_until_released_via_privacy_endpoints()
    {
        var (guest, _) = await CreateGuestWithStayAsync(
            isUnderLegalHold: false,
            yearsAgo: 1,
            notes: "Pending legal inquiry note",
            refundNotes: "Pending legal refund note",
            addOnNotes: "Pending legal add-on");
        var linkedClient = await _fixture.CreateUserAsync(UserRole.Client);

        var requestId = Guid.NewGuid();
        await _fixture.WithDbAsync(async db =>
        {
            var account = await db.Users.SingleAsync(user => user.Id == linkedClient.Id);
            account.GuestId = guest.Id;
            db.UserTokens.Add(new IdentityUserToken<Guid>
            {
                UserId = account.Id,
                LoginProvider = "ErasureTest",
                Name = "refresh-token",
                Value = "identity-token-secret"
            });
            db.UserLogins.Add(new IdentityUserLogin<Guid>
            {
                UserId = account.Id,
                LoginProvider = "ErasureTest",
                ProviderKey = $"provider-{account.Id:N}",
                ProviderDisplayName = "Erasure test login"
            });
            db.UserClaims.Add(new IdentityUserClaim<Guid>
            {
                UserId = account.Id,
                ClaimType = "guest-email",
                ClaimValue = account.Email
            });
            db.PrivacyRequests.Add(new PrivacyRequest
            {
                Id = requestId,
                GuestId = guest.Id,
                Type = DataSubjectRequestType.Erasure,
                Status = DataSubjectRequestStatus.Pending,
                Details = "Right to erasure request.",
                RequestedAtUtc = DateTime.UtcNow,
                DueAtUtc = DateTime.UtcNow.AddDays(30)
            });
            await db.SaveChangesAsync();
            return true;
        });

        var reception = await _fixture.CreateUserAsync(UserRole.Staff, "Reception");

        // 1. Non-admin staff (Reception) cannot place a legal hold (Forbidden)
        using (var forbiddenHold = AuthorizedJson(
            HttpMethod.Post,
            $"/api/privacy/guests/{guest.Id}/legal-hold",
            reception,
            new { reason = "Unauthorized staff attempt" }))
        using (var response = await _fixture.Client.SendAsync(forbiddenHold))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // 2. Admin places a legal hold
        using (var placeHold = AuthorizedJson(
            HttpMethod.Post,
            $"/api/privacy/guests/{guest.Id}/legal-hold",
            _fixture.Admin,
            new { reason = "Active regulatory compliance audit" }))
        using (var response = await _fixture.Client.SendAsync(placeHold))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("isUnderLegalHold").GetBoolean());
            Assert.Equal("Active regulatory compliance audit", json.RootElement.GetProperty("reason").GetString());
        }

        // 3. Admin attempts to fulfill erasure while under legal hold -> BadRequest (rejected)
        var fulfillmentPayload = new
        {
            status = "completed",
            resolutionNotes = "Verified and fulfilling erasure request.",
            identityVerificationReference = "ID-VERIFIED-1",
            fulfillmentEvidenceReference = "DOC-PROOF-1",
            confirmAction = true
        };

        using (var eraseAttempt = AuthorizedJson(
            HttpMethod.Patch,
            $"/api/privacy/requests/{requestId}/status",
            _fixture.Admin,
            fulfillmentPayload))
        using (var response = await _fixture.Client.SendAsync(eraseAttempt))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("legal hold", content, StringComparison.OrdinalIgnoreCase);
        }

        // 4. Admin releases the legal hold
        using (var releaseHold = AuthorizedJson(
            HttpMethod.Delete,
            $"/api/privacy/guests/{guest.Id}/legal-hold",
            _fixture.Admin,
            new { reason = "Audit successfully concluded without findings" }))
        using (var response = await _fixture.Client.SendAsync(releaseHold))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("isUnderLegalHold").GetBoolean());
        }

        // 5. Admin executes erasure now that hold is released -> OK
        using (var eraseSuccess = AuthorizedJson(
            HttpMethod.Patch,
            $"/api/privacy/requests/{requestId}/status",
            _fixture.Admin,
            fulfillmentPayload))
        using (var response = await _fixture.Client.SendAsync(eraseSuccess))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 6. Verify database reflects anonymization and scrubbed notes
        await _fixture.WithDbAsync(async db =>
        {
            var updatedGuest = await db.Guests.SingleAsync(g => g.Id == guest.Id);
            Assert.NotNull(updatedGuest.AnonymizedAtUtc);
            Assert.StartsWith("anonymized-", updatedGuest.Email);

            var booking = await db.Bookings.SingleAsync(b => b.GuestId == guest.Id);
            Assert.Null(booking.RefundNotes);
            Assert.Null(booking.GuestAccessTokenHash);
            var account = await db.Users.AsNoTracking()
                .SingleAsync(user => user.Id == linkedClient.Id);
            Assert.NotNull(account.AnonymizedAtUtc);
            Assert.Equal(ProfileStatus.Suspended, account.Status);
            Assert.Null(account.PasswordHash);
            Assert.False(await db.UserTokens.AnyAsync(token => token.UserId == linkedClient.Id));
            Assert.False(await db.UserLogins.AnyAsync(login => login.UserId == linkedClient.Id));
            Assert.False(await db.UserClaims.AnyAsync(claim => claim.UserId == linkedClient.Id));
            return true;
        });
    }

    [Fact]
    public async Task Reservation_amendment_queues_amendment_confirmation_in_same_transaction_with_deterministic_id()
    {
        var booking = await _fixture.CreateBookingAsync();
        var start = DateTime.UtcNow.Date.AddDays(30);
        var end = start.AddDays(4);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingService>();
        var amendments = scope.ServiceProvider.GetRequiredService<IReservationAmendmentService>();

        var quote = await pricing.CreateAmendmentQuoteAsync(booking.Id, new CreatePricingQuoteRequest(
            booking.RoomId, start, end, 2, 0), _fixture.Manager.Id);

        var amendmentResult = await amendments.AmendAsync(
            booking.Id,
            new AmendReservationRequest(
                quote.QuoteId, quote.QuoteToken, booking.RoomId, booking.RoomTypeId,
                1, start, end, 2, 0, "Guest requested stay extension by two days."),
            _fixture.Manager.Id);

        // Verify that a BookingAmendmentConfirmation email outbox entry was queued within the transaction
        var outboxMessage = await _fixture.WithDbAsync(db => db.EmailOutboxMessages
            .SingleOrDefaultAsync(m => m.Template == TransactionalEmailTemplates.BookingAmendmentConfirmation));

        Assert.NotNull(outboxMessage);
        Assert.Equal(0, outboxMessage.AttemptCount);
        Assert.Null(outboxMessage.QuarantinedAtUtc);
        Assert.Equal(booking.GuestId, outboxMessage.DataSubjectGuestId);

        var outbox = scope.ServiceProvider.GetRequiredService<IEmailOutbox>();
        var payload = outbox.ReadPayload<BookingAmendmentConfirmationEmail>(outboxMessage);

        Assert.Equal(booking.BookingCode, payload.BookingCode);
        Assert.Equal(quote.TotalAmount, payload.TotalAmount);
        Assert.Equal(amendmentResult.PriceDifference, payload.PriceDifference);
        Assert.Contains("booking-status", payload.ManageBookingUrl);
        Assert.Equal("Guest requested stay extension by two days.", payload.AmendmentReason);
    }

    [Fact]
    public async Task Folio_payments_credits_and_refunds_queue_durable_receipts_with_idempotency()
    {
        var booking = await _fixture.CreateBookingAsync(
            paymentMethod: PaymentMethod.DirectTransfer,
            paymentStatus: PaymentStatus.Unpaid,
            bookingStatus: BookingStatus.Pending,
            amount: 200000m);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var folios = scope.ServiceProvider.GetRequiredService<IFolioService>();
        var outbox = scope.ServiceProvider.GetRequiredService<IEmailOutbox>();

        // 1. Post a Cash payment
        var paymentKey = $"test-pmt-{Guid.NewGuid():N}";
        var folioAfterPayment = await folios.PostPaymentAsync(
            booking.BookingCode,
            new PostFolioPaymentRequest(
                50000m,
                "Cash",
                "CASH-RCP-1001",
                paymentKey,
                ConfirmReservation: false,
                Notes: "Cash deposit at front desk"),
            _fixture.Admin.Id);

        Assert.Equal(50000m, folioAfterPayment.Payments);

        // 2. Retry the EXACT SAME payment -> Idempotency prevents duplicate folio entry & duplicate email
        var retryPayment = await folios.PostPaymentAsync(
            booking.BookingCode,
            new PostFolioPaymentRequest(
                50000m,
                "Cash",
                "CASH-RCP-1001",
                paymentKey,
                ConfirmReservation: false,
                Notes: "Cash deposit at front desk"),
            _fixture.Admin.Id);

        Assert.Equal(50000m, retryPayment.Payments);

        var paymentReceipts = await _fixture.WithDbAsync(db => db.EmailOutboxMessages
            .Where(m => m.Template == TransactionalEmailTemplates.FolioReceipt)
            .Where(m => m.DataSubjectGuestId == booking.GuestId)
            .ToListAsync());

        Assert.Single(paymentReceipts);
        Assert.Equal(booking.GuestId, paymentReceipts[0].DataSubjectGuestId);
        var paymentReceiptPayload = outbox.ReadPayload<FolioReceiptEmail>(paymentReceipts[0]);
        Assert.Equal("Payment", paymentReceiptPayload.EntryType);
        Assert.Equal("Cash", paymentReceiptPayload.PaymentMethod);
        Assert.Equal(50000m, paymentReceiptPayload.Amount);
        Assert.Equal(booking.BookingCode, paymentReceiptPayload.BookingCode);

        // 3. Post a Discretionary Credit
        var creditKey = $"test-crd-{Guid.NewGuid():N}";
        await folios.PostCreditAsync(
            booking.BookingCode,
            new PostFolioCreditRequest(
                10000m,
                "Manager courtesy discount",
                creditKey,
                Notes: "Courtesy credit for corporate guest"),
            _fixture.Admin.Id);

        // 4. Check that credit receipt is queued
        var receiptsAfterCredit = await _fixture.WithDbAsync(db => db.EmailOutboxMessages
            .Where(m => m.Template == TransactionalEmailTemplates.FolioReceipt)
            .Where(m => m.DataSubjectGuestId == booking.GuestId)
            .ToListAsync());

        Assert.Equal(2, receiptsAfterCredit.Count);
        Assert.All(receiptsAfterCredit, receipt =>
            Assert.Equal(booking.GuestId, receipt.DataSubjectGuestId));
        var creditReceipt = receiptsAfterCredit.Single(r => r.Id != paymentReceipts[0].Id);
        var creditReceiptPayload = outbox.ReadPayload<FolioReceiptEmail>(creditReceipt);
        Assert.Equal("Credit", creditReceiptPayload.EntryType);
        Assert.Equal(10000m, creditReceiptPayload.Amount);

        // 5. Apply a refund to unsettled credit on a cancelled stay
        var refundBooking = await _fixture.CreateBookingAsync(
            paymentMethod: PaymentMethod.DirectTransfer,
            paymentStatus: PaymentStatus.RefundPending,
            bookingStatus: BookingStatus.Cancelled,
            amount: 100000m);

        await folios.ApplyRefundAsync(
            refundBooking.Id,
            50000m,
            $"REF-RECEIPT-{Guid.NewGuid():N}"[..20],
            "BankTransfer",
            "Refund for cancelled stay",
            _fixture.Admin.Id);

        var receiptsAfterRefund = await _fixture.WithDbAsync(db => db.EmailOutboxMessages
            .Where(m => m.Template == TransactionalEmailTemplates.FolioReceipt)
            .Where(m => m.DataSubjectGuestId == booking.GuestId ||
                        m.DataSubjectGuestId == refundBooking.GuestId)
            .ToListAsync());

        Assert.Equal(3, receiptsAfterRefund.Count);
        var refundReceipt = receiptsAfterRefund.Single(r => r.Id != paymentReceipts[0].Id && r.Id != creditReceipt.Id);
        Assert.Equal(refundBooking.GuestId, refundReceipt.DataSubjectGuestId);
        var refundPayload = outbox.ReadPayload<FolioReceiptEmail>(refundReceipt);
        Assert.Equal("Refund", refundPayload.EntryType);
        Assert.Equal(50000m, refundPayload.Amount);
    }

    [Fact]
    public async Task Role_authorization_matrix_enforces_least_privilege_across_all_staff_roles()
    {
        var booking = await _fixture.CreateBookingAsync();

        // Instantiate test actors across all operational departments
        var admin = _fixture.Admin;
        var manager = _fixture.Manager;
        var finance = await _fixture.CreateUserAsync(UserRole.Staff, "Finance");
        var cashier = await _fixture.CreateUserAsync(UserRole.Staff, "Cashier");
        var reception = await _fixture.CreateUserAsync(UserRole.Staff, "Reception");
        var concierge = await _fixture.CreateUserAsync(UserRole.Staff, "Concierge");
        var housekeeping = await _fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var engineering = await _fixture.CreateUserAsync(UserRole.Staff, "Engineering");

        // 1. Folio Payments: Admin, Finance, Cashier ALLOWED. Reception, Concierge, Housekeeping, Engineering FORBIDDEN.
        var paymentPayload = new
        {
            amount = 5000m,
            method = "Cash",
            externalReference = $"AUTHPAY-{Guid.NewGuid():N}",
            idempotencyKey = $"pmt-auth-{Guid.NewGuid():N}",
            confirmReservation = false
        };

        // Allowed
        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/payments", finance, paymentPayload, HttpStatusCode.OK);
        paymentPayload = paymentPayload with { externalReference = $"AUTHPAY-{Guid.NewGuid():N}", idempotencyKey = $"pmt-auth-{Guid.NewGuid():N}" };
        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/payments", cashier, paymentPayload, HttpStatusCode.OK);

        // Forbidden
        paymentPayload = paymentPayload with { externalReference = $"AUTHPAY-{Guid.NewGuid():N}", idempotencyKey = $"pmt-auth-{Guid.NewGuid():N}" };
        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/payments", reception, paymentPayload, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/payments", concierge, paymentPayload, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/payments", housekeeping, paymentPayload, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/payments", engineering, paymentPayload, HttpStatusCode.Forbidden);

        // 2. Folio Adjustments / Discretionary Credits: Admin & Manager ALLOWED. Finance, Cashier, Reception, Concierge FORBIDDEN.
        var creditPayload = new
        {
            amount = 1000m,
            reason = "Manager approved discount",
            idempotencyKey = $"crd-auth-{Guid.NewGuid():N}"
        };

        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/credits", manager, creditPayload, HttpStatusCode.OK);
        creditPayload = creditPayload with { idempotencyKey = $"crd-auth-{Guid.NewGuid():N}" };
        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/credits", finance, creditPayload, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/credits", cashier, creditPayload, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/credits", reception, creditPayload, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Post, $"/api/folios/{booking.BookingCode}/credits", concierge, creditPayload, HttpStatusCode.Forbidden);

        // 3. Guest CRM Profile: Admin, Manager, Reception ALLOWED. Finance, Cashier, Concierge, Housekeeping, Engineering FORBIDDEN.
        await AssertStatusAsync(HttpMethod.Get, $"/api/guest-crm/{booking.GuestId}", reception, null, HttpStatusCode.OK);
        await AssertStatusAsync(HttpMethod.Get, $"/api/guest-crm/{booking.GuestId}", finance, null, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Get, $"/api/guest-crm/{booking.GuestId}", cashier, null, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Get, $"/api/guest-crm/{booking.GuestId}", concierge, null, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Get, $"/api/guest-crm/{booking.GuestId}", housekeeping, null, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Get, $"/api/guest-crm/{booking.GuestId}", engineering, null, HttpStatusCode.Forbidden);

        // 4. Housekeeping Tasks: Admin, Manager, Housekeeping ALLOWED. Cashier, Concierge, Engineering FORBIDDEN.
        await AssertStatusAsync(HttpMethod.Get, "/api/housekeeping/tasks", housekeeping, null, HttpStatusCode.OK);
        await AssertStatusAsync(HttpMethod.Get, "/api/housekeeping/tasks", cashier, null, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Get, "/api/housekeeping/tasks", concierge, null, HttpStatusCode.Forbidden);

        // 5. Maintenance Work Orders: Admin, Manager, Engineering ALLOWED. Reception, Concierge, Cashier FORBIDDEN.
        await AssertStatusAsync(HttpMethod.Get, "/api/maintenance/work-orders", engineering, null, HttpStatusCode.OK);
        await AssertStatusAsync(HttpMethod.Get, "/api/maintenance/work-orders", reception, null, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Get, "/api/maintenance/work-orders", cashier, null, HttpStatusCode.Forbidden);

        // 6. Pricing Configuration: Admin & Manager ALLOWED. Staff roles FORBIDDEN.
        await AssertStatusAsync(HttpMethod.Get, "/api/pricing/configuration", manager, null, HttpStatusCode.OK);
        await AssertStatusAsync(HttpMethod.Get, "/api/pricing/configuration", finance, null, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Get, "/api/pricing/configuration", reception, null, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Get, "/api/pricing/configuration", housekeeping, null, HttpStatusCode.Forbidden);

        // 7. Channels Management: Admin & Manager ALLOWED. Staff roles FORBIDDEN.
        await AssertStatusAsync(HttpMethod.Get, "/api/channels", admin, null, HttpStatusCode.OK);
        await AssertStatusAsync(HttpMethod.Get, "/api/channels", manager, null, HttpStatusCode.OK);
        await AssertStatusAsync(HttpMethod.Get, "/api/channels", finance, null, HttpStatusCode.Forbidden);
        await AssertStatusAsync(HttpMethod.Get, "/api/channels", reception, null, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Financial_routes_reject_invalid_inputs_and_conflicting_refunds()
    {
        var booking = await _fixture.CreateBookingAsync();

        // 1. Payment with negative amount returns 400 Bad Request
        using (var invalidAmount = AuthorizedJson(
            HttpMethod.Post,
            $"/api/folios/{booking.BookingCode}/payments",
            _fixture.Admin,
            new
            {
                amount = -5000m,
                method = "Cash",
                externalReference = "INV-AMOUNT-001",
                idempotencyKey = $"pmt-invalid-{Guid.NewGuid():N}",
                confirmReservation = false
            }))
        using (var response = await _fixture.Client.SendAsync(invalidAmount))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // 2. Payment exceeding outstanding balance returns 400 Bad Request
        using (var excessiveAmount = AuthorizedJson(
            HttpMethod.Post,
            $"/api/folios/{booking.BookingCode}/payments",
            _fixture.Admin,
            new
            {
                amount = 999999999m,
                method = "Cash",
                externalReference = "INV-EXCESSIVE-001",
                idempotencyKey = $"pmt-excessive-{Guid.NewGuid():N}",
                confirmReservation = false
            }))
        using (var response = await _fixture.Client.SendAsync(excessiveAmount))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // 3. Payment with unsupported method returns 400 Bad Request
        using (var invalidMethod = AuthorizedJson(
            HttpMethod.Post,
            $"/api/folios/{booking.BookingCode}/payments",
            _fixture.Admin,
            new
            {
                amount = 5000m,
                method = "InvalidPaymentMethod",
                externalReference = "INV-METHOD-001",
                idempotencyKey = $"pmt-method-{Guid.NewGuid():N}",
                confirmReservation = false
            }))
        using (var response = await _fixture.Client.SendAsync(invalidMethod))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // 4. Conflicting refund reference with differing amount returns 409 Conflict
        var refundBooking = await _fixture.CreateBookingAsync(
            paymentMethod: PaymentMethod.DirectTransfer,
            paymentStatus: PaymentStatus.RefundPending,
            bookingStatus: BookingStatus.Cancelled,
            amount: 100000m);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var folios = scope.ServiceProvider.GetRequiredService<IFolioService>();

        var refId = $"CONFLICT-REF-{Guid.NewGuid():N}"[..20];
        await folios.ApplyRefundAsync(
            refundBooking.Id, 30000m, refId, "Cash", "Initial refund portion", _fixture.Admin.Id);

        // Reusing same reference with different amount -> ConflictException (409)
        await Assert.ThrowsAsync<MooreHotels.Application.Exceptions.ConflictException>(async () =>
        {
            await folios.ApplyRefundAsync(
                refundBooking.Id, 40000m, refId, "Cash", "Conflicting amount", _fixture.Admin.Id);
        });
    }

    private async Task AssertStatusAsync(
        HttpMethod method,
        string path,
        TestUser actor,
        object? body,
        HttpStatusCode expectedStatus)
    {
        using var request = body is not null
            ? AuthorizedJson(method, path, actor, body)
            : Authorized(method, path, actor);
        using var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
    }

    private async Task<(Guest Guest, Booking Booking)> CreateGuestWithStayAsync(
        bool isUnderLegalHold,
        int yearsAgo,
        string notes,
        string refundNotes,
        string addOnNotes)
    {
        return await _fixture.WithDbAsync(async db =>
        {
            var unique = Guid.NewGuid().ToString("N").ToUpperInvariant();
            var stayDate = DateTime.UtcNow.AddYears(-yearsAgo);

            var guest = new Guest
            {
                Id = $"GS-{unique[..16]}",
                FirstName = "Test",
                LastName = "Subject",
                Email = $"test-{unique[..8].ToLowerInvariant()}@moore-audit.test",
                Phone = "+2348011223344",
                NormalizedEmail = $"TEST-{unique[..8].ToUpperInvariant()}@MOORE-AUDIT.TEST",
                NormalizedPhone = "+2348011223344",
                CreatedAt = stayDate.AddDays(-30),
                IsUnderLegalHold = isUnderLegalHold,
                LegalHoldPlacedAtUtc = isUnderLegalHold ? DateTime.UtcNow : null,
                LegalHoldReason = isUnderLegalHold ? "Active legal hold investigation" : null,
                LegalHoldPlacedByUserId = isUnderLegalHold ? _fixture.Admin.Id : null
            };

            var roomType = new RoomType
            {
                Id = Guid.NewGuid(),
                Code = $"HRO-{unique[..8]}",
                Name = "Hardening Audit Room Type",
                Category = RoomCategory.Standard,
                BaseOccupancy = 1,
                MaxOccupancy = 2,
                BasePricePerNight = 50000m,
                Description = "Room for hardening audit tests",
                Amenities = ["Wi-Fi"]
            };

            var room = new Room
            {
                Id = Guid.NewGuid(),
                RoomTypeId = roomType.Id,
                RoomNumber = $"H-{unique[..8]}",
                Name = $"Hardening Room {unique}",
                Category = RoomCategory.Standard,
                Floor = PropertyFloor.GroundFloor,
                Status = RoomStatus.Available,
                PricePerNight = 50000m,
                Capacity = 2,
                Size = "28 sqm",
                IsOnline = true,
                Description = "Hardening audit test room",
                Amenities = ["Wi-Fi"]
            };

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                BookingCode = $"MHS{Random.Shared.Next(100000, 999999)}",
                RoomId = room.Id,
                RoomTypeId = roomType.Id,
                GuestId = guest.Id,
                CheckIn = stayDate.AddDays(-2),
                CheckOut = stayDate,
                AdultCount = 1,
                ChildCount = 0,
                Status = BookingStatus.CheckedOut,
                Currency = "NGN",
                RoomSubtotal = 100000m,
                Amount = 100000m,
                PaymentStatus = PaymentStatus.Paid,
                PaymentMethod = PaymentMethod.DirectTransfer,
                Notes = notes,
                RefundNotes = refundNotes,
                GuestAccessTokenHash = BookingGuestAccess.Hash("test-token"),
                CreatedAt = stayDate.AddDays(-2)
            };

            var addOn = new AddOnService
            {
                Id = Guid.NewGuid(),
                Name = "Airport Pickup",
                Price = 15000m,
                Category = AddOnCategory.Transportation,
                Description = "Private airport transfer",
                IsActive = true
            };

            var bookingAddOn = new BookingAddOn
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AddOnServiceId = addOn.Id,
                Quantity = 1,
                UnitPrice = 15000m,
                TotalPrice = 15000m,
                Notes = addOnNotes,
                AddedAtUtc = stayDate.AddDays(-2)
            };

            var folio = FolioAccounting.CreateInitial(booking, stayDate.AddDays(-2));
            folio.Entries.Add(FolioAccounting.NewEntry(
                folio, FolioEntryType.Payment, FolioEntryDirection.Credit,
                booking.Amount, "Settlement payment", "Audit", booking.Id.ToString(),
                $"test-pmt:{booking.Id:N}", stayDate.AddDays(-2),
                externalReference: $"TESTPAY-{unique}"));
            folio.Status = FolioStatus.Closed;
            folio.ClosedAtUtc = stayDate;
            folio.ClosedByUserId = _fixture.Admin.Id;

            db.RoomTypes.Add(roomType);
            db.Rooms.Add(room);
            db.Guests.Add(guest);
            db.Bookings.Add(booking);
            db.AddOnServices.Add(addOn);
            db.BookingAddOns.Add(bookingAddOn);
            db.Folios.Add(folio);

            await db.SaveChangesAsync();
            return (guest, booking);
        });
    }

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
