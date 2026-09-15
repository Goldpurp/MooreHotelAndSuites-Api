using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MooreHotels.WebAPI.Services;
using Microsoft.Extensions.Options;
using MooreHotels.WebAPI.Configuration;
using MooreHotels.Domain.Common;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly MooreHotelsDbContext _context;
    private readonly OperationalReadinessSettings _operations;
    private readonly ProviderAcceptanceSettings _providerAcceptance;
    private readonly MonnifySettings _monnify;
    private readonly LaunchGateSettings _launchGate;

    public HealthController(
        MooreHotelsDbContext context,
        IOptions<OperationalReadinessSettings> operations,
        IOptions<ProviderAcceptanceSettings> providerAcceptance,
        IOptions<MonnifySettings> monnify,
        IOptions<LaunchGateSettings> launchGate)
    {
        _context = context;
        _operations = operations.Value;
        _providerAcceptance = providerAcceptance.Value;
        _monnify = monnify.Value;
        _launchGate = launchGate.Value;
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Check()
    {
        try
        {
            var canConnect = await _context.Database.CanConnectAsync();
            if (!canConnect)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    Status = "Unhealthy",
                    Timestamp = DateTimeOffset.UtcNow,
                    Database = "Disconnected"
                });
            }

            var pendingEmails = await _context.EmailOutboxMessages
                .AsNoTracking()
                .CountAsync(message => message.AttemptCount < 12);
            var exhaustedEmails = await _context.EmailOutboxMessages
                .AsNoTracking()
                .CountAsync(message => message.AttemptCount >= 12);
            var pendingMediaDeletions = await _context.MediaDeletionJobs
                .AsNoTracking()
                .CountAsync(job => job.AttemptCount < MediaDeletionWorker.MaximumAttempts);
            var exhaustedMediaDeletions = await _context.MediaDeletionJobs
                .AsNoTracking()
                .CountAsync(job => job.AttemptCount >= MediaDeletionWorker.MaximumAttempts);
            var oldestPendingEmailAtUtc = await _context.EmailOutboxMessages
                .AsNoTracking()
                .Where(message => message.AttemptCount < 12)
                .MinAsync(message => (DateTime?)message.CreatedAtUtc);
            var oldestPendingMediaDeletionAtUtc = await _context.MediaDeletionJobs
                .AsNoTracking()
                .Where(job => job.AttemptCount < MediaDeletionWorker.MaximumAttempts)
                .MinAsync(job => (DateTime?)job.CreatedAtUtc);
            var failedPaymentsLastDay = await _context.MonnifyTransactions
                .AsNoTracking()
                .CountAsync(transaction =>
                    transaction.Status == "FAILED" &&
                    transaction.CreatedAt >= DateTime.UtcNow.AddDays(-1));
            var stalePendingPayments = await _context.MonnifyTransactions
                .AsNoTracking()
                .CountAsync(transaction =>
                    transaction.Status == "PENDING" &&
                    transaction.CreatedAt <= DateTime.UtcNow.AddMinutes(
                        -_operations.PaymentPendingWarningMinutes));

            var now = DateTimeOffset.UtcNow;
            var emailQueueAgeMinutes = GetAgeMinutes(oldestPendingEmailAtUtc, now);
            var mediaQueueAgeMinutes = GetAgeMinutes(oldestPendingMediaDeletionAtUtc, now);
            var restoreDrillAgeDays = _operations.LastRestoreDrillAtUtc.HasValue
                ? Math.Max(0, (now - _operations.LastRestoreDrillAtUtc.Value).TotalDays)
                : (double?)null;
            var restoreDrillCurrent = restoreDrillAgeDays.HasValue &&
                                      restoreDrillAgeDays <= _operations.RestoreDrillMaximumAgeDays;
            var emailQueueHealthy = exhaustedEmails == 0 &&
                                    emailQueueAgeMinutes.GetValueOrDefault() <=
                                    _operations.QueueAgeWarningMinutes;
            var mediaQueueHealthy = exhaustedMediaDeletions == 0 &&
                                    mediaQueueAgeMinutes.GetValueOrDefault() <=
                                    _operations.QueueAgeWarningMinutes;
            var paymentHealth = failedPaymentsLastDay == 0 && stalePendingPayments == 0;
            var alertsAccepted = _operations.UptimeAlertsEnabled &&
                                 _operations.ApiErrorAndLatencyAlertsEnabled &&
                                 _operations.QueueAgeAlertsEnabled &&
                                 _operations.PaymentAndWebhookAlertsEnabled &&
                                 !string.IsNullOrWhiteSpace(_operations.AlertRoutingEvidenceReference);
            var providersAccepted = IsAccepted(_providerAcceptance.Cloudinary) &&
                                    IsAccepted(_providerAcceptance.Brevo) &&
                                    (!_monnify.Enabled ||
                                     (IsAccepted(_providerAcceptance.MonnifySandbox) &&
                                      IsAccepted(_providerAcceptance.MonnifyWebhook) &&
                                      IsAccepted(_providerAcceptance.MonnifyLivePaymentAndRefund) &&
                                      IsAccepted(_providerAcceptance.PciResponsibilityReview) &&
                                      _providerAcceptance.HostedPaymentPageOnly));

            var response = new
            {
                Status = emailQueueHealthy && mediaQueueHealthy && paymentHealth && restoreDrillCurrent &&
                         alertsAccepted && providersAccepted && !_launchGate.Enabled
                    ? "Healthy"
                    : "Degraded",
                Timestamp = now,
                Database = "Connected",
                Launch = new
                {
                    AcceptingGuestTraffic = !_launchGate.Enabled,
                    ValidationMode = _launchGate.Enabled
                },
                Alerts = new
                {
                    Accepted = alertsAccepted,
                    _operations.UptimeAlertsEnabled,
                    _operations.ApiErrorAndLatencyAlertsEnabled,
                    _operations.QueueAgeAlertsEnabled,
                    _operations.PaymentAndWebhookAlertsEnabled,
                    _operations.AlertRoutingEvidenceReference
                },
                EmailQueue = new
                {
                    Status = emailQueueHealthy ? "Operational" : "AttentionRequired",
                    Pending = pendingEmails,
                    Exhausted = exhaustedEmails,
                    OldestPendingAgeMinutes = emailQueueAgeMinutes,
                    WarningThresholdMinutes = _operations.QueueAgeWarningMinutes
                },
                MediaDeletionQueue = new
                {
                    Status = mediaQueueHealthy ? "Operational" : "AttentionRequired",
                    Pending = pendingMediaDeletions,
                    Exhausted = exhaustedMediaDeletions,
                    OldestPendingAgeMinutes = mediaQueueAgeMinutes,
                    WarningThresholdMinutes = _operations.QueueAgeWarningMinutes
                },
                Payments = new
                {
                    Status = paymentHealth ? "Operational" : "AttentionRequired",
                    FailedLast24Hours = failedPaymentsLastDay,
                    StalePending = stalePendingPayments,
                    PendingWarningThresholdMinutes = _operations.PaymentPendingWarningMinutes
                },
                Recovery = new
                {
                    Status = restoreDrillCurrent ? "Verified" : "AttentionRequired",
                    _operations.BackupMode,
                    _operations.ManagedBackupsEnabled,
                    _operations.PointInTimeRecoveryEnabled,
                    _operations.EncryptedOffProviderBackupsEnabled,
                    _operations.RecoveryPointObjectiveMinutes,
                    _operations.RecoveryTimeObjectiveMinutes,
                    _operations.LastRestoreDrillAtUtc,
                    RestoreDrillAgeDays = restoreDrillAgeDays,
                    _operations.RestoreDrillMaximumAgeDays,
                    _operations.RestoreDrillEvidenceReference
                },
                Providers = new
                {
                    Brevo = ToAcceptanceStatus(_providerAcceptance.Brevo),
                    Cloudinary = ToAcceptanceStatus(_providerAcceptance.Cloudinary),
                    Monnify = new
                    {
                        Enabled = _monnify.Enabled,
                        HostedPaymentPageOnly = _providerAcceptance.HostedPaymentPageOnly,
                        Sandbox = ToAcceptanceStatus(_providerAcceptance.MonnifySandbox),
                        Webhook = ToAcceptanceStatus(_providerAcceptance.MonnifyWebhook),
                        LivePaymentAndRefund = ToAcceptanceStatus(
                            _providerAcceptance.MonnifyLivePaymentAndRefund),
                        PciResponsibilityReview = ToAcceptanceStatus(
                            _providerAcceptance.PciResponsibilityReview)
                    }
                }
            };

            // Delivery failures require an operational alert, but they must not
            // remove a database-connected API instance from the load balancer.
            return Ok(response);
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Status = "Unhealthy",
                Timestamp = DateTimeOffset.UtcNow,
                Database = "Disconnected"
            });
        }
    }

    private static double? GetAgeMinutes(DateTime? createdAtUtc, DateTimeOffset now) =>
        createdAtUtc.HasValue
            ? Math.Round(
                Math.Max(0, (now.UtcDateTime - createdAtUtc.Value).TotalMinutes),
                1,
                MidpointRounding.AwayFromZero)
            : null;

    private static object ToAcceptanceStatus(AcceptanceEvidence evidence) => new
    {
        Accepted = IsAccepted(evidence),
        evidence.AcceptedAtUtc,
        evidence.EvidenceReference,
        evidence.CredentialRotationReference
    };

    private static bool IsAccepted(AcceptanceEvidence evidence) =>
        evidence.AcceptedAtUtc.HasValue &&
        !string.IsNullOrWhiteSpace(evidence.EvidenceReference) &&
        !string.IsNullOrWhiteSpace(evidence.CredentialRotationReference);

    [HttpGet("~/health/ready")]
    [HttpHead("~/health/ready")]
    [AllowAnonymous]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        try
        {
            return await _context.Database.CanConnectAsync(cancellationToken)
                ? Ok(new
                {
                    Status = "Ready",
                    Timestamp = DateTimeOffset.UtcNow,
                    Database = "Connected"
                })
                : StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    Status = "NotReady",
                    Timestamp = DateTimeOffset.UtcNow,
                    Database = "Disconnected"
                });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Status = "NotReady",
                Timestamp = DateTimeOffset.UtcNow,
                Database = "Disconnected"
            });
        }
    }
}
