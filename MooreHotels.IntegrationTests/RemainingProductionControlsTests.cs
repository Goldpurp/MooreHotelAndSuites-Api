using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Hubs;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class RemainingProductionControlsTests
{
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly ManualTransferTestFixture _fixture;

    public RemainingProductionControlsTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Suspending_staff_immediately_removes_every_tracked_realtime_connection()
    {
        var staff = await _fixture.CreateUserAsync(UserRole.Staff);
        var stamp = await _fixture.WithDbAsync(db => db.Users
            .Where(user => user.Id == staff.Id)
            .Select(user => user.SecurityStamp!)
            .SingleAsync());
        var registry = _fixture.Services.GetRequiredService<StaffConnectionRegistry>();
        var groups = new RecordingGroupManager();

        Assert.True(await registry.TryRegisterAsync(
            staff.Id,
            "test-connection",
            stamp,
            groups));
        Assert.Equal(1, registry.GetActiveConnectionCount(staff.Id));
        Assert.Contains("test-connection", groups.Added);

        using var request = AuthorizedJsonRequest(
            HttpMethod.Patch,
            $"/api/admin/management/accounts/{staff.Id}/status",
            _fixture.Admin,
            new { status = "Suspended" });
        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, registry.GetActiveConnectionCount(staff.Id));
        Assert.False(await registry.TryRegisterAsync(
            staff.Id,
            "stale-connection",
            stamp,
            groups));
    }

    [Fact]
    public async Task Security_sensitive_self_service_change_revokes_staff_realtime_access()
    {
        var staff = await _fixture.CreateUserAsync(UserRole.Staff);
        var stamp = await _fixture.WithDbAsync(db => db.Users
            .Where(user => user.Id == staff.Id)
            .Select(user => user.SecurityStamp!)
            .SingleAsync());
        var registry = _fixture.Services.GetRequiredService<StaffConnectionRegistry>();
        var groups = new RecordingGroupManager();
        Assert.True(await registry.TryRegisterAsync(
            staff.Id,
            "self-service-connection",
            stamp,
            groups));

        using var update = AuthorizedJsonRequest(
            HttpMethod.Put,
            "/api/profile/me",
            staff,
            new
            {
                email = $"changed-{Guid.NewGuid():N}@example.test",
                currentPassword = "TransferTest123!"
            });
        using var response = await _fixture.Client.SendAsync(update);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, registry.GetActiveConnectionCount(staff.Id));
        Assert.False(await registry.TryRegisterAsync(
            staff.Id,
            "stale-self-service-connection",
            stamp,
            groups));
    }

    [Fact]
    public async Task Managed_image_detach_and_deletion_job_commit_together_then_worker_cleans_storage()
    {
        using var upload = AuthorizedRequest(
            HttpMethod.Post,
            "/api/images/upload?folder=general",
            _fixture.Admin);
        var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(PngBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "file", "durable-delete.png");
        upload.Content = form;
        using var uploadResponse = await _fixture.Client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        using var uploadJson = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync());
        var publicId = uploadJson.RootElement.GetProperty("publicId").GetString()!;
        var localPath = _fixture.GetLocalAssetPath(publicId);
        Assert.True(File.Exists(localPath));

        using var delete = AuthorizedRequest(
            HttpMethod.Delete,
            $"/api/images/delete?publicId={Uri.EscapeDataString(publicId)}",
            _fixture.Manager);
        using var deleteResponse = await _fixture.Client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.Accepted, deleteResponse.StatusCode);

        var queued = await _fixture.WithDbAsync(async db => new
        {
            AssetCount = await db.MediaAssets.CountAsync(asset => asset.PublicId == publicId),
            JobCount = await db.MediaDeletionJobs.CountAsync(job => job.PublicId == publicId)
        });
        Assert.Equal(0, queued.AssetCount);
        Assert.Equal(1, queued.JobCount);
        Assert.True(File.Exists(localPath));

        await _fixture.FlushMediaDeletionOutboxAsync();
        Assert.False(File.Exists(localPath));
        Assert.Equal(0, await _fixture.WithDbAsync(db =>
            db.MediaDeletionJobs.CountAsync(job => job.PublicId == publicId)));
    }

    [Fact]
    public async Task Admin_reconciles_an_unlinked_client_with_identity_evidence_and_audit()
    {
        var client = await _fixture.CreateUserAsync(UserRole.Client);
        var email = await _fixture.WithDbAsync(db => db.Users
            .Where(user => user.Id == client.Id)
            .Select(user => user.Email!)
            .SingleAsync());
        var unique = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var guest = new Guest
        {
            Id = $"GS-{unique[..16]}",
            FirstName = "Reconcile",
            LastName = "Guest",
            Email = email,
            Phone = "+2348000000022"
        };
        await _fixture.WithDbAsync(async db =>
        {
            db.Guests.Add(guest);
            await db.SaveChangesAsync();
            return true;
        });

        using (var issues = AuthorizedRequest(
                   HttpMethod.Get,
                   "/api/admin/client-guest-links/issues?pageSize=100",
                   _fixture.Admin))
        using (var issuesResponse = await _fixture.Client.SendAsync(issues))
        {
            Assert.Equal(HttpStatusCode.OK, issuesResponse.StatusCode);
            Assert.Contains(client.Id.ToString(), await issuesResponse.Content.ReadAsStringAsync());
        }

        using (var mismatchedEvidence = AuthorizedJsonRequest(
                   HttpMethod.Post,
                   $"/api/admin/client-guest-links/{client.Id}/reconcile",
                   _fixture.Admin,
                   new
                   {
                       guestId = guest.Id,
                       evidenceType = "VerifiedPhone",
                       reason = "The phone evidence must match both records before linking."
                   }))
        using (var mismatchedEvidenceResponse = await _fixture.Client.SendAsync(mismatchedEvidence))
        {
            Assert.Equal(HttpStatusCode.BadRequest, mismatchedEvidenceResponse.StatusCode);
        }

        using var reconcile = AuthorizedJsonRequest(
            HttpMethod.Post,
            $"/api/admin/client-guest-links/{client.Id}/reconcile",
            _fixture.Admin,
            new
            {
                guestId = guest.Id,
                evidenceType = "VerifiedEmail",
                reason = "Matched the verified account email to the guest record."
            });
        using var response = await _fixture.Client.SendAsync(reconcile);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var state = await _fixture.WithDbAsync(async db => new
        {
            GuestId = await db.Users
                .Where(user => user.Id == client.Id)
                .Select(user => user.GuestId)
                .SingleAsync(),
            AuditCount = await db.AuditLogs.CountAsync(log =>
                log.Action == "CLIENT_GUEST_RECONCILED" &&
                log.EntityId == client.Id.ToString())
        });
        Assert.Equal(guest.Id, state.GuestId);
        Assert.Equal(1, state.AuditCount);
    }

    [Fact]
    public async Task Refunds_require_complete_evidence_unique_references_and_dual_control_when_high_value()
    {
        var lowValue = await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.RefundPending,
            bookingStatus: BookingStatus.Cancelled);
        var reference = $"REFUND-{Guid.NewGuid():N}";
        using (var complete = RefundRequest(lowValue.Id, _fixture.Admin, reference, lowValue.Amount))
        using (var response = await _fixture.Client.SendAsync(complete))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var storedLowValue = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(booking => booking.Id == lowValue.Id));
        Assert.Equal(PaymentStatus.Refunded, storedLowValue.PaymentStatus);
        Assert.Equal(lowValue.Amount, storedLowValue.RefundAmount);
        Assert.Equal("BankTransfer", storedLowValue.RefundChannel);
        Assert.Equal("BankStatement", storedLowValue.RefundEvidenceType);
        Assert.Equal(_fixture.Admin.Id, storedLowValue.RefundProcessedByUserId);
        Assert.NotNull(storedLowValue.RefundProcessedAtUtc);

        var duplicate = await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.RefundPending,
            bookingStatus: BookingStatus.Cancelled);
        using (var duplicateRequest = RefundRequest(
                   duplicate.Id,
                   _fixture.Manager,
                   reference,
                   duplicate.Amount))
        using (var duplicateResponse = await _fixture.Client.SendAsync(duplicateRequest))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        }

        var highValue = await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.RefundPending,
            bookingStatus: BookingStatus.Cancelled,
            amount: 600000m);

        using (var noApproval = RefundRequest(
                   highValue.Id,
                   _fixture.Admin,
                   $"REFUND-{Guid.NewGuid():N}",
                   600000m))
        using (var noApprovalResponse = await _fixture.Client.SendAsync(noApproval))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noApprovalResponse.StatusCode);
        }

        using (var approval = AuthorizedJsonRequest(
                   HttpMethod.Post,
                   $"/api/bookings/{highValue.Id}/approve-refund",
                   _fixture.Admin,
                   new { reason = "Verified the refund request and original payment record." }))
        using (var approvalResponse = await _fixture.Client.SendAsync(approval))
        {
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
        }

        var highReference = $"REFUND-{Guid.NewGuid():N}";
        using (var sameOperator = RefundRequest(
                   highValue.Id,
                   _fixture.Admin,
                   highReference,
                   600000m))
        using (var sameOperatorResponse = await _fixture.Client.SendAsync(sameOperator))
        {
            Assert.Equal(HttpStatusCode.BadRequest, sameOperatorResponse.StatusCode);
        }

        using (var independentOperator = RefundRequest(
                   highValue.Id,
                   _fixture.Manager,
                   highReference,
                   600000m))
        using (var independentResponse = await _fixture.Client.SendAsync(independentOperator))
        {
            Assert.Equal(HttpStatusCode.OK, independentResponse.StatusCode);
        }

        var storedHighValue = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(booking => booking.Id == highValue.Id));
        Assert.Equal(_fixture.Admin.Id, storedHighValue.RefundApprovedByUserId);
        Assert.Equal(_fixture.Manager.Id, storedHighValue.RefundProcessedByUserId);
    }

    private static HttpRequestMessage RefundRequest(
        Guid bookingId,
        TestUser actor,
        string reference,
        decimal amount) =>
        AuthorizedJsonRequest(
            HttpMethod.Post,
            $"/api/bookings/{bookingId}/complete-refund",
            actor,
            new
            {
                transactionReference = reference,
                amount,
                channel = "BankTransfer",
                evidenceType = "BankStatement",
                notes = "Matched to the bank settlement statement."
            });

    private static HttpRequestMessage AuthorizedJsonRequest(
        HttpMethod method,
        string path,
        TestUser actor,
        object body)
    {
        var request = AuthorizedRequest(method, path, actor);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        TestUser actor)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public HashSet<string> Added { get; } = new(StringComparer.Ordinal);

        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            Added.Add(connectionId);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            Added.Remove(connectionId);
            return Task.CompletedTask;
        }
    }
}
