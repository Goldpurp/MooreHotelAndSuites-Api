# Moore Hotels API — Render production deployment

This is the required release procedure for the API. Do not add a production
`.env` file to the repository. Render environment variables, secret files and
the Supabase PostgreSQL project are the production source of truth.

## 1. Infrastructure

- Use a Supabase PostgreSQL project in the intended production region. Enable
  the required backup/PITR plan before accepting guest data.
- Use a paid Starter-or-higher API web service in the same region.
- Attach the persistent disk defined in `render.yaml` at `/var/data`.
- Keep the Supabase project and Render environment under production-owned
  organization accounts with MFA and separate administrator access.
- Configure the API health check as `/health/ready`.
- Keep automatic deploy set to **After CI checks pass**.

The Docker image contains a reviewed EF Core migration bundle and PostgreSQL
client. Render runs `scripts/predeploy-production.sh`, which first rejects a
Local/test/restore database target and any existing non-Production environment
stamp, closes the Supabase Data API surface, validates existing data, applies
the bundle with `MIGRATION_CONNECTION_STRING`, binds the database to
`production`, revalidates and re-hardens the schema, and then refreshes runtime
DML grants for `DATABASE_RUNTIME_ROLE` before starting the new version.
The running API uses the separate least-privileged
`ConnectionStrings__DefaultConnection`; startup removes the migration variable
from its process before configuration is built, does not apply schema changes,
and fails closed if the database is unavailable.

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

Before the first deployment:

1. Create a new Supabase project; do not reuse the expired Render database or
   copy its credentials. Record the IPv4 **session pooler** connection shown by
   Supabase under **Connect**. Render must use port 5432, not the transaction
   pooler on port 6543.
2. Generate a unique runtime password in a password manager, then create (or
   rotate) the dedicated runtime role with the Supabase `postgres` owner
   connection:

   ```bash
   MIGRATION_CONNECTION_STRING='...' \
   DATABASE_RUNTIME_ROLE='moore_runtime' \
   DATABASE_RUNTIME_PASSWORD='...' \
   ./scripts/create-supabase-runtime-role.sh
   ```

   Remove `DATABASE_RUNTIME_PASSWORD` immediately afterward; it is not a
   Render runtime setting. The runtime role cannot own database objects or
   receive role-management, database-creation, replication, superuser, or
   `BYPASSRLS` privileges.
3. Put the dedicated runtime connection in
   `ConnectionStrings__DefaultConnection` using Npgsql key/value syntax:

   ```text
   Host=<region>.pooler.supabase.com;Port=5432;Database=postgres;Username=moore_runtime.<project-ref>;Password=...;Maximum Pool Size=20;Timeout=10;Command Timeout=30;SSL Mode=VerifyFull
   ```

   `Maximum Pool Size` is per API instance and is fail-closed above 32. Keep
   the sum across all instances within the Supabase project/pooler connection
   budget. Npgsql multiplexing is not permitted with this session-pooler setup.
4. Configure `MIGRATION_CONNECTION_STRING` with the Supabase `postgres` owner
   through the same session pooler and `SSL Mode=VerifyFull`, using the same
   semicolon-separated Npgsql key/value syntax shown above rather than a URI.
   Use it only for Render's pre-deploy command and migration rehearsal. The
   application clears this variable before building runtime configuration.
   `scripts/bind-production-database.sh` additionally requires the production
   Supabase session-pooler host, port 5432 and `VerifyFull`; it rejects database
   names marked local, development, test, restore, drill, or rehearsal. Its
   `MOORE_PRODUCTION_REHEARSAL=true` escape hatch is CI-only and itself requires
   an explicitly named test/rehearsal database.
5. Run the grant script once during setup and verify the runtime credential:

   ```bash
   MIGRATION_CONNECTION_STRING='...' \
   DATABASE_RUNTIME_ROLE='moore_runtime' \
   ./scripts/provision-runtime-database-role.sh

   RUNTIME_CONNECTION_STRING='...' \
   ./scripts/validate-runtime-database-role.sh
   ```

   The same provisioning runs after every production migration so new tables
   do not accidentally become inaccessible or over-privileged. Pre-deploy also
   removes all `public` schema, table, sequence, and function access from
   Supabase's `anon`, `authenticated`, `service_role`, and `authenticator`
   roles. The browser applications must never contain a Supabase URL, anon key,
   service key, or direct database credential; they call this API only.
   The runtime role receives `SELECT` only on `environment_boundaries`; it
   cannot insert, update, delete, truncate, reference, trigger, or use the
   marker's sequence. Production startup requires the singleton marker to be
   exactly `production` before it serves requests.
6. Keep network restrictions, organization MFA, database alerts, managed
   backups, and PITR configured in Supabase. Use a direct IPv6 connection for
   administration only when the runner supports it; Render uses the IPv4
   session pooler.
7. Take a backup of an existing database before every migration.
8. Rehearse the migration against a restored copy before the production deploy.
9. Run `MIGRATION_CONNECTION_STRING='...' ./scripts/validate-production-database.sh`
   against the restored copy before applying the migration bundle.
10. After restoring the current release, run
    `RESTORE_DRILL_CONNECTION_STRING='...' ./scripts/verify-restore-drill.sh`.
    Use PostgreSQL backup tools at least as new as the source server's major
    version. The verifier requires all application/Identity tables, a production
    environment marker, matching release migration and valid business data.
    When migration sources are unavailable, set
    `RESTORE_DRILL_EXPECTED_MIGRATION` explicitly. Compare recovered record
    counts/hashes and sequence values with the backup manifest as well.

Complete `docs/DISASTER_RECOVERY_AND_ALERTING.md` before entering the
`OperationalReadiness__*` declarations. Production startup requires managed
backups, PITR, an encrypted off-provider copy, all four alert categories, alert
routing evidence and restore-drill evidence. The authenticated health endpoint
reports queue age, exhausted work, payment warnings and restore-drill age.

Do not paste database credentials into GitHub, chat, tickets, screenshots or
application logs.

## 3. Rotate and enter secrets

The previously used database, JWT, Brevo, Cloudinary and administrator
credentials must be rotated. Enter the new values only in Render:

- `ConnectionStrings__DefaultConnection`
- `MIGRATION_CONNECTION_STRING`
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

The image pre-creates `/var/data/moorehotels-keys` as mode `0700` owned by the
non-root `app` user. Keep the Render disk mounted at `/var/data`; changing the
mount or key path can make it unwritable. Production startup performs a
storage write/flush check, validates every retained non-revoked key, and performs
an encrypted protect/unprotect round-trip. It refuses to serve traffic if the
persistent path, certificate or password is unusable. An existing key on a
read-only mount is not sufficient. After the first staging start, restart the
service and confirm `/health/ready` remains 200 and the same
key file remains on the disk.

Also verify that a payload protected before restart can still be unprotected
afterward and after restoring the key-ring backup. Preserve the original PFX
and password with that backup. Do not replace the certificate in isolation:
rehearse any certificate/key-ring migration first, because a different valid
certificate does not decrypt data protected by the existing keys.

Keep the image's default entrypoint. It removes `MIGRATION_CONNECTION_STRING`
and `DATABASE_RUNTIME_PASSWORD` before executing .NET, including from Linux's
initial process environment. Clearing these only inside application code does
not remove that initial snapshot. The separate pre-deploy command retains its
owner credential. Operators with hosting-platform access still control the
configured secrets; this boundary protects the serving API process.

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
- Provider-managed PITR, encrypted off-provider export and every alert route
  have been tested; the latest quarterly restore evidence is visible in
  authenticated operational health.
