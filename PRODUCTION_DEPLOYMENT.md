# Moore Hotels API — Render production deployment

This is the required release procedure for the API. Do not add a production
`.env` file to the repository. Render environment variables, secret files and
the attached PostgreSQL database are the production source of truth.

## 1. Infrastructure

- Use a paid Render PostgreSQL database in the Frankfurt region.
- Use a paid Starter-or-higher API web service in the same region.
- Attach the persistent disk defined in `render.yaml` at `/var/data`.
- Keep the database and API in the same Render project/environment.
- Configure the API health check as `/health/ready`.
- Keep automatic deploy set to **After CI checks pass**.

The Docker image contains a reviewed EF Core migration bundle and PostgreSQL
client. Render runs `scripts/predeploy-production.sh`, which validates existing
data, applies the bundle with `MIGRATION_CONNECTION_STRING`, and refreshes
runtime DML grants for `DATABASE_RUNTIME_ROLE` before starting the new version.
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
setup. Before scaling to multiple instances, add and test a shared SignalR
backplane plus a cross-instance revocation channel.

## 2. Database

Before the first deployment:

1. Create or upgrade the paid PostgreSQL database.
2. Create two separate database logins with an administrative database
   credential: a migration owner and a runtime login. The migration login must
   own the database's `public` schema; the runtime login must not own the
   database, schema, tables, sequences, or migrations history. Set
   `DATABASE_RUNTIME_ROLE` to the runtime role name. The checked-in pre-deploy
   script refuses to continue if it cannot remove schema creation from that
   role.
3. Keep the API and database in the same Render account and Frankfurt region,
   then copy the database's **internal** host, port, database, username and
   password into `ConnectionStrings__DefaultConnection` using Npgsql key/value
   syntax:

   ```text
   Host=...;Port=5432;Database=...;Username=...;Password=...;Maximum Pool Size=80;Timeout=10;Command Timeout=30
   ```

   Render internal connections stay on its private network and do not use the
   public database allowlist. If the API and database are not in the same
   account and region, stop and correct that instead of using the slower
   external URL for production.
4. Configure `MIGRATION_CONNECTION_STRING` with the database/schema owner. Use
   that credential only for Render's pre-deploy command and migration rehearsal.
5. Run the grant script once during setup and verify the runtime credential:

   ```bash
   MIGRATION_CONNECTION_STRING='...' \
   DATABASE_RUNTIME_ROLE='moore_runtime' \
   ./scripts/provision-runtime-database-role.sh

   RUNTIME_CONNECTION_STRING='...' \
   ./scripts/validate-runtime-database-role.sh
   ```

   The same provisioning runs after every production migration so new tables
   do not accidentally become inaccessible or over-privileged.
6. Disable public/external database access unless an approved administration or
   backup system genuinely requires it.
7. Take a backup of an existing database before every migration.
8. Rehearse the migration against a restored copy before the production deploy.
9. Run `MIGRATION_CONNECTION_STRING='...' ./scripts/validate-production-database.sh`
   against the restored copy before applying the migration bundle.

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
3. Deploy once and sign in.
4. Set `SeedAdmin=false`, remove the seed password, redeploy and change the
   administrator password.

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
- Registration and booking require the current policy versions, client data
  export works, privacy requests are audited through closure, and a rehearsed
  retention sweep anonymizes only expired unlinked guest records.
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
