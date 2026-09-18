# Disaster recovery and alerting runbook

This runbook is a production release gate. Configuration values are declarations
of completed external work, not switches that create backups or alerts. Never set
an evidence variable before the corresponding dashboard action and test have
actually passed.

## Recovery objectives

- RPO target: 1440 minutes for daily encrypted logical exports. This depends
  on the external scheduler completing; a missed export increases data-loss exposure.
- RTO: 240 minutes. The incident commander must be able to restore, validate,
  cut over, and reopen bookings within four hours.
- Backup mode: Logical on Supabase Free. Managed backups and PITR are disabled.
- Portable copy: encrypted, access-controlled, stored outside the database
  provider, and retained according to the approved records schedule.
- Restore drill: quarterly and after any material database/provider change.

## Initial setup

1. Configure the external encrypted export command documented in
   `PRODUCTION_DEPLOYMENT.md`. Keep the age private key and application PFX
   separately recoverable. Do not claim PITR is enabled.
2. Provision and validate the dedicated `moore_backup` role, then configure the
   repository's daily GitHub Actions export. Retain only age-encrypted artifacts
   within the free storage quota. Route its Healthchecks.io missed/failed-run
   notification to `moorehotelsandsuites@gmail.com` and
   `lilswatch112@gmail.com`, and trigger a manual failure test. Sleeping Render
   workers cannot schedule the export.
3. Configure uptime checks for `/health/live` and `/health/ready` from at least
   two regions. Page when two consecutive checks fail.
4. Configure 5xx-rate and p95-latency alerts, plus log alerts for rejected
   payment webhooks and payment reconciliation failures.
5. Poll authenticated `GET /api/health` from a secret-bearing internal monitor.
   Alert when `status=Degraded`, either queue exceeds its age threshold, work is
   exhausted, or payment warnings are nonzero. Never place the staff JWT in a URL.
6. Route warnings to the operating team and paging alerts to two named people.
   Trigger a test alert and record its evidence reference.
7. Enter the `OperationalReadiness__*` declarations and evidence references in
   the cloud secret/configuration manager. Evidence references may be ticket or
   vault record IDs; never put credentials or guest data in them.

The free launch profile uses UptimeRobot for five-minute availability and
operations checks. The default-branch `production-api-monitor.yml` workflow
adds an independent ten-minute synthetic check for HTTP failures and p95
latency above two seconds. The workflow excludes one warm-up request so a
Render Free cold start is reported by availability monitoring without
distorting warm-service latency. Treat a failed scheduled workflow as an API
alert and investigate it using the same response procedure below.

## Quarterly restore drill

1. Choose a recovery point within the RPO and record the production booking,
   guest, room, payment-ledger, and audit-log counts at that point.
2. Before restoring, create any runtime role referenced by the dump's RLS
   policies on the new isolated cluster (`CREATE ROLE moore_runtime NOLOGIN;`
   for the selected project, if absent). Decrypt and restore the logical
   snapshot there. Never overwrite production. Provision separate restore-only
   login credentials and grants before any subsequent API recovery test.
3. Name the target with `restore`, `drill`, or `rehearsal`; the verification
   script refuses any other database name.
4. Run:

   ```bash
   RESTORE_DRILL_CONNECTION_STRING='...' ./scripts/verify-restore-drill.sh
   ```

5. Compare the returned counts and newest booking timestamp with the recorded
   source manifest. Validate a sample reservation, guest, payment, refund, and
   audit trail. Do not send emails or call payment/media providers from the copy.
6. Rehearse the application connection-string cutover and rollback steps, then
   remove the isolated database after evidence is approved.
7. Copy `docs/RESTORE_DRILL_EVIDENCE_TEMPLATE.md`, complete it, store it in the
   approved evidence system, and update the timestamp/reference configuration.

## Incident recovery

1. Freeze deploys and payment/refund administration; identify the last trusted
   write and declare the incident commander.
2. Restore the latest verified encrypted logical export to a new isolated
   database. Reconcile writes since its timestamp; there is no PITR on this setup.
3. Run the verification script and compare business counts and sampled records.
4. Rotate affected database credentials, update every consumer atomically, and
   observe readiness, errors, queues, payments, and booking creation.
5. Reconcile provider transactions accepted during the affected interval before
   reopening online payment.
6. Preserve logs/evidence, notify stakeholders under the incident policy, and
   document actual RPO/RTO and follow-up actions.

## Required alert catalogue

| Signal | Initial threshold | Response |
|---|---:|---|
| `/health/ready` | 2 consecutive failures | Page operator |
| API 5xx | >2% for 5 minutes | Page operator |
| API p95 latency | >2 seconds for 10 minutes | Warn, then page if sustained |
| Email/media queue age | >15 minutes | Warn operator |
| Exhausted queue work | Any | Page operator and recover item |
| Failed Monnify transaction | Any in 24 hours | Reconcile and inspect provider |
| Pending Monnify transaction | >30 minutes | Reconcile before room release |
| Rejected/invalid webhook | Repeated or unexpected source | Security/payment investigation |
| Encrypted backup failure | Any | Page database owner |
| Missed off-provider export | One daily export | Page database owner |
