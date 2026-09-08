# Moore Hotels API — Render production deployment

This is the required release procedure for the API. Do not add a production
`.env` file to the repository. Render environment variables, secret files and
the Supabase PostgreSQL project are the production source of truth.

## 1. Free hosting

Use **Supabase Free** for PostgreSQL and **Render Free** only for the API.
The selected Supabase project is `ihxhqfffrxmgqabrncrg` (Moore hotel and suites,
Frankfurt). Render has no database resource, disk, or pre-deploy command in
`render.yaml`. Keep auto-deploy **Off**, and deploy a tested commit manually
only after its external migration finishes. Use `/health/ready` for the health
check. The existing service is `srv-d5uhns4oud1c73bn28o0`.

Render Free sleeps after idle periods, has cold starts and an ephemeral local
filesystem. Background email, expiration, and retention workers cannot run
while it sleeps; queued work resumes when the API wakes. Supabase Free can
also pause for inactivity. This setup does not promise continuous availability,
precise job timing, or point-in-time recovery. Monitor free usage quotas and
configure spending limits; a Free plan does not make excess usage unlimited.
See [Render Free limits](https://render.com/docs/free) and
[Supabase project pausing](https://supabase.com/docs/guides/platform/free-project-pausing).

Encryption keys persist in Supabase, encrypted using a PFX held separately in
Render secrets. No guest data, uploads, or key files depend on the web server's
local disk. The runtime uses only its least-privileged database login and never
applies migrations. Do not configure the owner credential on Render.

The image is pinned to .NET 10.0.11 and SDK 10.0.400. Framework packages and EF
tooling use 10.0.11, and Npgsql EF Core uses 10.0.3. Keep the monthly .NET 10
security patch, SDK feature band, framework packages, EF tool and lock files in
sync when updating this pin.

Keep this release at one API instance. Live SignalR connection revocation uses
the same process-local connection registry as the current SignalR broadcast
setup, and its one-minute handshake tickets are process-local and single-use.
Before scaling to multiple instances, add and test a shared SignalR backplane,
ticket store, and cross-instance revocation channel.

## 2. Database

Use the Supabase **session pooler** from the project's Connect panel, port
5432, with `SSL Mode=VerifyFull`. Do not use transaction mode (6543), a Render
Postgres hostname, or a Supabase API key as a database password.

Runtime connection (`ConnectionStrings__DefaultConnection` on Render):

```text
Host=<actual-pooler>.pooler.supabase.com;Port=5432;Database=postgres;Username=moore_runtime.ihxhqfffrxmgqabrncrg;Password=...;Maximum Pool Size=20;Timeout=10;Command Timeout=30;SSL Mode=VerifyFull
```

Set `Database__Provider=Supabase`, `DATABASE_RUNTIME_ROLE=moore_runtime`, and
`Database__ApplyMigrationsOnStartup=false`. The Local profile continues to use
isolated local PostgreSQL and cannot connect to the production project.

For each release, serially:

1. Run the release tests and build the exact image with
   `docker build -t moore-hotels-api:release .`.
2. Back up any existing application database and rehearse the migration on a
   restored copy before changing production. See the free backup procedure below.
3. Supply `MIGRATION_CONNECTION_STRING` (owner session-pooler login),
   `DATABASE_RUNTIME_ROLE`, and `RUNTIME_CONNECTION_STRING` securely to the
   external runner's environment. On initial bootstrap only, also supply
   `DATABASE_RUNTIME_PASSWORD` to create the dedicated role. Do not enter these
   values into shell history or commit them. Keep owner secrets off Render.
4. Run `./scripts/deploy-database.sh moore-hotels-api:release` on that machine.
   This uses the image's migration bundle, closes Data API access, validates
   business invariants, binds the database to production, provisions the runtime
   grants, and verifies the runtime login. Any failure stops the sequence.
5. Remove the migration/provisioning secrets from the runner environment. After
   success, manually deploy that same commit to the existing Render web service.
6. Verify readiness, account links, email queue processing, and restart recovery.

The API role cannot change schema or migration history. Its key-table access
is append-only, with RLS restricted to that role. Supabase Data API roles receive
no access to application tables. Browser clients call the API only.

### Free backup and recovery

Set `OperationalReadiness__BackupMode=Logical`,
`OperationalReadiness__ManagedBackupsEnabled=false`, and
`OperationalReadiness__PointInTimeRecoveryEnabled=false`. Logical mode replaces
only the paid managed/PITR requirement. An encrypted off-provider backup and a
successful restore drill are still required before accepting guest data.

Install PostgreSQL **17 or newer** client tools and [age](https://age-encryption.org/)
on a trusted machine outside Render. Create an age identity with `age-keygen`,
keep its private identity encrypted/offline, and use its public recipient for
backups. Supply `BACKUP_CONNECTION_STRING` via the runner's secret environment
and `BACKUP_AGE_RECIPIENT` with the public recipient, then run:

```bash
./scripts/backup-production.sh /secure/backups/moore-YYYY-MM-DD.dump.age
```

The script streams all public application/Identity tables, sequences, migration
history, and encrypted keys directly into age encryption. No plaintext dump is
written. It refuses insecure TLS and existing output files. The supplied
PostgreSQL client must be at least the source server's major version.
Keep copies on a separate device/provider and retain the PFX/password separately.
Schedule this on a trusted machine that is actually on; do not rely on a worker
inside a sleeping Render instance. Set the recovery-point objective to the real
backup interval, not a promised PITR interval. Scheduling/restore evidence must
be established by the operator before the readiness declarations become true.

To rehearse recovery, configure PostgreSQL client environment variables for a
**fresh isolated** database whose name contains `restore`, `drill`, or
`rehearsal`. Decrypt the archive into `pg_restore`:

```bash
age --decrypt --identity /secure/backup-identity.txt moore-YYYY-MM-DD.dump.age \
  | pg_restore --exit-on-error --clean --if-exists --no-owner --no-privileges --dbname="$PGDATABASE"
./scripts/verify-restore-drill.sh
```

The restore command removes conflicting schema objects, including the default
`public` schema; run it only against the fresh isolated target described above.
The verifier requires `RESTORE_DRILL_CONNECTION_STRING`. Recreate the dedicated
runtime role and grants on the restored target; the export intentionally omits
role passwords and ACLs. Verify record counts, business invariants and protected
payload decryption with the backed-up PFX. Never restore over production as a
drill. Logical backups recover only to the exported snapshot.

Complete `docs/DISASTER_RECOVERY_AND_ALERTING.md` and keep the backup, restore,
provider and alert declarations truthful. The authenticated health endpoint
reports the chosen backup mode and actual managed/PITR flags.

## 3. Rotate and enter secrets

The previously used database, JWT, Brevo, Cloudinary and administrator
credentials must be rotated. Enter the new values only in Render:

- `ConnectionStrings__DefaultConnection`
- `DATABASE_RUNTIME_ROLE`
- `CloudinarySettings__CloudName`
- `CloudinarySettings__ApiKey`
- `CloudinarySettings__ApiSecret`
- `EmailSettings__ApiPass`
- `EmailSettings__SenderEmail`
- `EmailSettings__AdminNotificationEmail`
- `BankTransferSettings__BankName`
- `BankTransferSettings__AccountName`
- `BankTransferSettings__AccountNumber`
- `FinancialControls__HighValueRefundThreshold`
- `OperationalReadiness__LastRestoreDrillAtUtc`
- `OperationalReadiness__RestoreDrillEvidenceReference`
- `OperationalReadiness__AlertRoutingEvidenceReference`
- all `ProviderAcceptance__*` evidence values required by enabled providers
- `Privacy__CurrentPrivacyPolicyVersion`
- `Privacy__CurrentBookingTermsVersion`
- `Privacy__PrivacyPolicyUrl`
- `Privacy__BookingTermsUrl`
- `Privacy__GuestRetentionDays`
- `Privacy__InactiveAccountRetentionDays`
- `DataProtection__CertificateBase64`
- `DataProtection__CertificatePassword`

`Jwt__Key` is generated by the Blueprint for a new service. For an existing
service, replace the old key manually with a new cryptographically random value.
All existing login tokens become invalid after rotation.

`ForwardedHeaders__TrustRenderEdge=true` is safe only while the container port
is reachable exclusively through Render's edge. Keep `ForwardLimit=1`. If the
service moves to another host or becomes directly reachable, disable this mode
and configure that host's exact trusted proxy addresses instead.

Before accepting real guest data, publish the approved privacy policy and
booking terms at the configured HTTPS URLs. Record approval of the configured
retention period; changing either document requires a new version value so new
acceptances remain attributable to the exact text shown.

## 4. Data Protection certificate

Generate this outside the repository:

```bash
openssl req -x509 -newkey rsa:3072 -sha256 -days 825 -nodes \
  -subj "/CN=Moore Hotels Data Protection" \
  -keyout moorehotels-data-protection.key \
  -out moorehotels-data-protection.crt

openssl pkcs12 -export \
  -out moorehotels-data-protection.pfx \
  -inkey moorehotels-data-protection.key \
  -in moorehotels-data-protection.crt
```

Encode the PFX as a single-line base64 value and enter it in Render as
`DataProtection__CertificateBase64`. On macOS use
`base64 -i moorehotels-data-protection.pfx | tr -d '\n'`; on Linux use
`base64 -w0 moorehotels-data-protection.pfx`. Enter its export password in
`DataProtection__CertificatePassword`. Store an encrypted offline backup of the
PFX and password. Never commit the PFX or encoded value.

Set `DataProtection__StorageProvider=Database`. The encrypted XML key ring is
stored in `public.data_protection_keys`, included in database backups. Startup
checks database writes using a rolled-back probe, validates every retained
non-revoked key, and checks protection/decryption before serving traffic.
It rejects an unreadable key ring or wrong certificate instead of silently
starting a replacement ring. Never replace the PFX independently of its keys.

After deployment, restart and confirm an earlier protected payload still
opens. Repeat that check against a restored backup with the original PFX and
password. When migrating an existing filesystem key ring, preserve/import its
XML and original PFX before switching storage; creating a new ring would
invalidate existing account links and encrypted outbox payloads. The selected
project was empty during the 2026-09-08 inspection; recheck before bootstrap.

Keep the default image entrypoint. It removes migration/provisioning variables
before starting the API as defense in depth. External migration commands use
a separate disposable container and never run as part of the serving process.

## 5. Monnify

Monnify is intentionally disabled:

```text
MonnifySettings__Enabled=false
```

The API rejects Monnify booking attempts while disabled, and direct bank
transfer remains available. Do not add provider credentials yet.

When the Monnify account is ready:

1. Test hosted checkout and server-side verification with provider-approved
   sandbox credentials.
2. Configure the completion webhook:
   `https://api.moorehotelandsuites.com/api/payments/monnify-webhook`
3. Enter the rotated API key, secret key and contract code in Render.
4. Confirm the documented webhook source IP and signature behavior.
5. Perform a controlled low-value transaction and refund.
6. Complete the PCI responsibility/AOC review and confirm every card-entry
   element remains on the provider-hosted page.
7. Enter every Monnify and PCI acceptance timestamp/reference and set
   `ProviderAcceptance__HostedPaymentPageOnly=true`.
8. Set `MonnifySettings__Enabled=true`.
9. Set `VITE_MONNIFY_ENABLED=true` in the guest website and rebuild it.

## 6. Brevo

- Rotate the exposed API key.
- Verify `EmailSettings__SenderEmail` and authenticate its sending domain.
- Add the API service's actual Render outbound IP ranges to Brevo.
- Send booking, cancellation, payment confirmation, expiry, password-reset and
  checkout messages from the deployed API.
- Confirm delivery in both Brevo logs and the receiving mailbox.
- Enter the Brevo credential-rotation, acceptance timestamp and evidence
  references. Complete equivalent upload/delete/retry evidence for Cloudinary.
  Follow `docs/PROVIDER_ACCEPTANCE_RUNBOOK.md`.

## 7. Initial administrator

For a brand-new empty database only:

1. Temporarily set `SeedAdmin=true`.
2. Set `AdminSeed__Email`, `AdminSeed__Password` and `AdminSeed__Name` in
   Render using a new one-time password.
3. Deploy, sign in, change the password and enroll MFA.
4. Repeat steps 2–3 with a separately controlled second administrator. The
   emergency access workflow requires another active, email-confirmed,
   MFA-enabled Admin and forbids self-suspension.
5. Set `SeedAdmin=false`, remove every seed password, and redeploy.

Ordinary account-status endpoints intentionally reject Admin targets. For a
confirmed compromise, another Admin uses the dedicated emergency
suspend/reactivate endpoint with their current password, current TOTP code,
target-email confirmation and an incident reference as the reason. Concurrent
actions are serialized, the last operational Admin is preserved, sessions are
revoked, and both suspension and reactivation are audited. Test this workflow
with non-production accounts before launch.

Do not reuse a password previously shared in chat.

## 8. Deployment acceptance

Do not deploy the dashboard or guest website until all checks pass:

- `/health/live` and `/health/ready` return HTTP 200.
- An authenticated Admin can access `/api/health`; anonymous callers receive
  `401`. It reports `Healthy` when the email and media-deletion queues are
  operational and `Degraded`—without taking the API out of service—when either
  queue has exhausted work.
- An untrusted CORS origin receives no access-control allow-origin header.
- Admin, Manager, department-scoped Staff and Client permissions behave
  correctly; a Housekeeping account cannot read guest or reservation lists.
- Booking requests persist adults and children, accept the exact room capacity,
  and reject one guest above capacity at both the API and database boundaries.
- The frontend requests a current quote, displays currency, each nightly rate,
  discounts, included/exclusive tax and fees, then submits the quote ID/token.
  Changed, expired, invalid and replayed quotes are rejected; the consumed
  breakdown remains unchanged after pricing configuration changes.
- Room types have reviewed codes, occupancy limits, base rates and physical-room
  mappings. Multi-room availability is correct across closures and overlapping
  reservations; reception can assign and move physical rooms before arrival.
- A newly created booking has one reservation-unit row per room and an opening
  folio. Split payments, later add-on charges, credits, immutable voids, partial
  refunds and zero-balance checkout have been acceptance-tested. Reconciliation
  uses folio entries, not a mutable booking total.
- The configured reservation-policy version and percentages match the approved
  guest terms. Test a free cancellation, a late cancellation, a no-show and a
  deposit confirmation; each booking must retain its original policy snapshot.
- Amend a future reservation with the booking-specific amendment-quote route
  and verify the original reservation is excluded from quote availability,
  inventory is locked, old pricing is voided, replacement pricing is posted,
  payment/credit state is recalculated and the amendment history cannot be
  updated or deleted.
- Checkout creates one dirty-room cleaning task per assigned room. Complete
  cleaning and inspection, verify only a passed inspection returns the room to
  sale, and verify a maintenance work order blocks its exact stay dates. Before
  launch, convert every legacy room still marked `Maintenance` into a dated work
  order/closure so historical availability has an attributable source record.
- Reconcile the hotel-local operational report to folio payments/refunds and
  occupied/available room nights. Close a test business date twice and verify
  that the same immutable night-audit snapshot is returned.
- Verify Reception can use the board/calendar without gaining housekeeping,
  maintenance, sensitive-note, merge, night-close or channel-admin privileges.
- Test duplicate detection and a controlled guest merge using documented
  evidence; confirm stays move to the primary profile and retention redacts
  preferences and staff-note content when the guest becomes eligible.
- Keep all distribution channels disabled until a named provider adapter has
  passed signature, replay/idempotency, inbound reservation, outbound inventory,
  retry/dead-letter and reconciliation acceptance. The generic authenticated
  event ledger is not itself a public OTA webhook.
- Registration and booking require the current policy versions, client data
  export works, privacy requests are audited through closure, and a rehearsed
  retention sweep anonymizes expired unlinked guests and linked Client accounts
  only after both the guest-stay and account-inactivity periods expire. Confirm
  recent clients, staff-linked guests, active requests and legal holds remain
  protected; confirm eligible accounts lose passwords, MFA data, external
  logins, claims, tokens and queued PII-bearing email.
- Room create/update/delete and image replacement work.
- Direct-transfer booking, exact `ACCEPT` confirmation and audit logging work.
- Anonymous booking requires a valid, single-use email-verification token;
  missing, expired, mismatched and replayed tokens are rejected.
- An unpaid booking expires after one hour and releases its room-type inventory.
  A provider payment received after expiry does not restore the reservation and
  creates refundable guest credit for staff review.
- All required emails are delivered.
- Secure booking links expire; an accepted rotation invalidates the old link
  and sends a two-hour replacement. Duplicate requests within one minute are
  suppressed, cancellation revokes the link, and code-plus-email alone cannot
  read any booking.
- `GET /api/admin/client-guest-links/issues` is empty; every reconciliation was
  performed by an Admin with recorded evidence and an audit entry.
- Low-value refunds require an exact amount, channel, matching evidence type and
  unique external reference. Refunds at or above
  `FinancialControls__HighValueRefundThreshold` were approved and completed by
  two different active Admin/Manager accounts.
- Replacing or deleting managed media creates a durable deletion job; simulate
  one provider failure, verify retry, and verify exhausted jobs appear in the
  administrator recovery endpoint.
- `scripts/check-nuget-vulnerabilities.sh` reports that the release dependency
  gate passed.
- Logs contain no passwords, JWTs, API keys or full connection strings.
- Rollback and database restore procedures have been rehearsed.
- Encrypted logical export/restore and every alert route have been tested; the latest quarterly restore evidence is visible in
  authenticated operational health.
