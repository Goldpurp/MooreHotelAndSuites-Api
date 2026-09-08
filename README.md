# Moore Hotels & Suites API

The API supports exactly two isolated runtime profiles: `Local` and
`Production`. Local reads the ignored `.env.local` file. Production continues
to read its values from the hosting platform's environment/secret manager and
never loads local secrets.

## First-time Local setup

1. Install PostgreSQL once. The current Local profile uses `127.0.0.1:5433`
   and the `postgres` login. The project launcher starts the existing local
   PostgreSQL cluster automatically when it is not already running.
2. Copy `.env.local.example` to the ignored `.env.local` and replace its
   placeholders. Keep this file owner-readable only (`chmod 600 .env.local`).
3. Start the API in release-style Local mode:

```bash
bash scripts/run-local.sh
```

For hot reload during development, use:

```bash
bash scripts/watch-local.sh
```

Use these guarded launchers instead of running `dotnet watch run` directly.
They start or reuse PostgreSQL, wait until port `5433` is ready, and only then
launch the API. After a computer restart, no separate database command is
required.

During Local startup the API connects briefly to PostgreSQL's existing
`postgres` maintenance database. If `moore_hotels_local` is missing, it creates
it, applies every EF Core migration, creates the Identity roles, and provisions
the configured initial administrator. Database creation is rejected in every
non-Local environment.

Local accepts only PostgreSQL on loopback (or an explicitly enabled,
single-label test-container host) and a database name containing a distinct
`local`, `dev`, or `test` segment. The first startup permanently stamps that
database `local`; startup stops before serving traffic if the stored stamp is
`production`. Production pre-deploy performs the inverse check and will not
bind or migrate a Local-stamped database. Local and Production also use
different JWT issuer/audience pairs and Data Protection application names.

The API listens on `http://127.0.0.1:5222`; local Swagger is available at
`/swagger`. `/health/live` reports process liveness, `/health/ready` reports
database readiness, and `/api/health` reports detailed operational health.

The Local staff login is:

- Email: the `AdminSeed__Email` value in `.env.local`
- Password: the `AdminSeed__Password` value in `.env.local`

Those values are intentionally ignored by Git. `SeedAdmin=true` creates the
administrator only when it does not already exist; changing the environment
file later does not overwrite an existing account password.

## How the applications connect

| Client | Local | Production |
|---|---|---|
| Staff dashboard | `/api` through port 3000 → API 5222 | Direct HTTPS API |
| Guest website | `/api` through port 3001 → API 5222 | Direct HTTPS API |
| Backend jobs/webhooks | Direct Local API URL | Direct Production API URL |

Browser profiles send `X-Moore-App-Environment`; the API returns
`X-Moore-API-Environment` and rejects a Local/Production mismatch. CORS is
restricted to the configured browser origins.

## Transactional email (Brevo)

Local is capture-only and cannot call Brevo, Cloudinary, Monnify or another
external provider. This prevents local tests, seeded accounts and sample data
from affecting production systems. Production Brevo configuration belongs only
in the cloud secret manager:

- `EmailSettings__DeliveryMode=Brevo`
- `EmailSettings__ApiPass`
- `EmailSettings__SenderEmail`
- `EmailSettings__SenderName`
- `EmailSettings__AdminNotificationEmail`
- `EmailSettings__MaxRetryAttempts=3`

The sender address must exist as a verified Brevo sender. Authenticate its
domain in Brevo as well so messages are not rejected or routed to spam.
Production startup fails with a clear configuration error when Brevo mode has a
missing key, invalid sender/admin address, or placeholder value. Local startup
fails if Brevo mode or external provider access is enabled. Never commit or log
the API key.

The API sends lifecycle messages for:

- public-booking email verification before inventory is reserved;
- a new booking (guest receipt and operations alert);
- Monnify or manually acknowledged transfer payment success;
- staff or guest cancellation, no-show, and automatic one-hour payment expiry;
- refund action and refund completion;
- checkout thank-you;
- account verification, password reset, staff onboarding, suspension and
  reactivation.

Booking lifecycle email is committed to a Data-Protection-encrypted database
outbox in the same transaction as the booking, payment, cancellation, refund or
checkout change. A multi-instance-safe worker retries delivery with a stable
provider idempotency key and removes the protected payload after Brevo accepts
it. `/api/health` reports queue counts without exposing guest data. Brevo calls
also use bounded provider responses and limited retries for timeouts, rate
limiting, and server errors. Automated Local integration tests use
`DeliveryMode=Capture`, replace the delivery service in memory, and assert every
lifecycle trigger without sending test mail to real recipients. `Capture` is
rejected in Production.

## Swagger

Swagger remains available in Local at `/swagger` for endpoint testing. Its
generated OpenAPI document strips operation summaries, operation descriptions,
schema/property descriptions, tag descriptions, parameter descriptions, and
security-scheme descriptions, so the expanded endpoint cards do not show the
long descriptive blocks from XML comments. Production Swagger remains disabled.

## Database changes

Production keeps automatic startup migrations disabled. Generate and review an
idempotent deployment script, back up the database, apply it as a separate
release step, then start the new API build:

```bash
dotnet tool restore
dotnet tool run dotnet-ef migrations script --idempotent \
  --project MooreHotels.Infrastructure \
  --startup-project MooreHotels.Infrastructure
```

The current `ProductionHardening` migration removes obsolete sensitive fields
and adds integrity constraints/indexes. `ProductionUserGuestLink` adds the
nullable, unique user-to-guest relationship required by account profiles.
`ManualTransferTypedAcknowledgement` adds the server-owned manual-confirmation
metadata and confirming-user relationship. The migration chain is exercised
from an empty PostgreSQL database by the integration suite.
`SecureMonnifyPayments` binds provider references to bookings and adds the
idempotent transaction ledger. `DurableMonnifyCheckout` lets a verified guest
resume an unexpired hosted checkout. `RemoveMonnifyLedgerPii` removes redundant
guest name/e-mail fields from the payment ledger.
`RandomBookingCodeAllocations` replaces volume-revealing sequential references
with collision-safe random references and reserves every existing booking code
against reuse.
`SecureGuestAccessAndEmailOutbox` adds secure guest-management tokens and the
encrypted transactional email outbox. `HardenRemainingProductionControls`
adds expiring guest access, durable media deletion, refund evidence/approval
metadata and the indexes and constraints used by the final production gates.
`CompleteProductionReadinessSevenToTen` adds rate plans, room/category daily
rates, taxes/fees, promotions, expiring quotes, immutable nightly price lines,
booking price snapshots and the latest recovery/provider readiness controls.
`CompleteHotelOperationsAndDistribution` adds snapshotted reservation policies,
append-only reservation amendments, housekeeping and maintenance work orders,
hotel-local operational reports and night audits, guest CRM merge evidence, and
the durable provider-neutral channel event/reconciliation ledger.

## Public booking references

New bookings receive a reference in the form `MHS` plus six cryptographically
random digits, for example `MHS482071`. The digits do not represent a database
row count, date, customer count, or booking volume. A database primary-key
reservation makes allocation atomic across concurrent requests and multiple API
instances. Existing references are preserved and are never reissued.

New public bookings are managed through a 256-bit secure link that expires
after 24 hours. The API stores only its hash and validity window; it does not
retain a reversible copy, and the raw token is never written to audit logs.
The browser sends the token in `X-Booking-Access-Token`, not an API URL, and
email links put it in the URL fragment so it is not sent to the website host or
CDN. Booking lookup uses `POST /api/bookings/lookup` with `{ "code": "..." }`
in JSON; neither guest email nor the token belongs in a query string. A
non-enumerating access-link request rotates the token and emails a
two-hour replacement. Code plus email alone cannot read or cancel either new or
historical bookings, and cancellation revokes the active link.

Anonymous guests must first call `POST /api/bookings/verification/request` with
their email address. The single-use token delivered by email is submitted as
`emailVerificationToken` on `POST /api/bookings`; it expires after 15 minutes
and is consumed in the same transaction that reserves the room. Authenticated
Client accounts with a linked guest profile do not need this extra step.

## Pricing and immutable quotes

The guest flow calls `POST /api/pricing/quotes` before creating a booking. The
quote selects an active rate plan, applies the most-specific daily rate (room
then room type, then category), calculates promotions, included tax, exclusive tax and fees,
and returns a 256-bit quote token once. Only its hash is stored. Submit
`quoteId` and `quoteToken` with exactly the same room type, quantity, dates and occupancy to
`POST /api/bookings` before the configured 15-minute expiry.

Production requires a quote. Consumption, promotion redemption and the room
reservation are revalidated under database locks and committed together. The
booking stores currency and aggregate amounts; its related immutable quote
retains each nightly rate and every adjustment for booking views and invoices.
Unconsumed expired quotes are removed after a one-day diagnostic window. See
`docs/PRICING_AND_QUOTES.md` for the complete client and staff contract.

New reservations sell room types and can reserve up to ten physical units under
one booking. Reception assigns or moves the actual rooms before arrival. Every
booking also has an append-only folio supporting deposits, split payments,
later charges, credits, immutable voids, and partial refunds; checkout requires
a zero balance. See `docs/INVENTORY_AND_FOLIOS.md` for API and launch guidance.

Reservation policy terms are copied onto each booking so a later configuration
change cannot rewrite an existing guest agreement. Staff can amend an unstarted
reservation only with a current immutable quote; the old folio pricing is
reversed, the replacement pricing is posted, and the before/after record is
append-only. Checkout marks assigned rooms dirty, opens cleaning tasks, and
requires a passed inspection before a room returns to `Available`. Maintenance
work orders create date-based inventory closures.

The operations API exposes a hotel-local arrivals/departures board, reservation
calendar, room-only ADR/RevPAR, payment/refund reconciliation, receivables and
immutable daily night-audit snapshots. Guest CRM supports verified-contact
evidence, preferences, restricted notes and controlled duplicate merges.
Channel records/events are intentionally provider-neutral and disabled until a
specific channel adapter, its webhook signature verification, sandbox tests and
reconciliation acceptance are complete. See
`docs/HOTEL_OPERATIONS_AND_CHANNELS.md`.

## Manual bank-transfer confirmation

Authenticated `Admin` and `Manager` accounts confirm a pending
`DirectTransfer` booking with:

```http
POST /api/bookings/{bookingCode}/confirm-transfer
Content-Type: application/json

{
  "confirmationText": "ACCEPT",
  "confirmationMethod": "TypedAcknowledgement",
  "transactionReference": "LEGACY-VALUE-IGNORED"
}
```

`confirmationText` must match `ACCEPT` exactly, including case and whitespace.
The API ignores the compatibility `transactionReference`, locks and re-checks
the booking row, generates its own `MANUAL-{BOOKING_CODE}-{SERVER_ID}`
reference, and commits the booking metadata and secret-free audit record in one
transaction. Paid, cancelled, refunded, refund-pending, non-transfer, and other
non-awaiting bookings are rejected instead of being treated as successful.

## Monnify hosted checkout

Monnify is an explicit opt-in feature. Keep
`MonnifySettings__Enabled=false` until provider credentials, hosted checkout,
server verification, webhook behavior and a controlled refund have passed.
While disabled, the API rejects Monnify booking requests before guest or
booking data is created. Direct bank transfer remains available.

The guest website never receives Monnify credentials and never decides that a
payment succeeded. The API creates a unique merchant payment reference,
initializes the hosted checkout, validates the returned reference and HTTPS
Monnify URL, then stores both provider references. Browser redirects are used
only to return the guest to the booking-status page.

Payment is credited only after the API independently queries Monnify and binds
all of these values to the stored booking: merchant reference, provider
reference, booking code, optional booking ID, guest e-mail, NGN currency,
successful status, amount paid, and a reasonable payment timestamp. The booking
update, payment ledger, status history, and secret-free audit entry commit in
one database transaction. A locked booking row and unique database indexes make
webhook retries and concurrent staff verification idempotent.

Configure these values only in the hosting platform's secret manager:

- `MonnifySettings__ApiKey`
- `MonnifySettings__SecretKey`
- `MonnifySettings__ContractCode`
- `MonnifySettings__BaseUrl=https://api.monnify.com`
- `MonnifySettings__EnforceWebhookIpAllowlist=true`
- `MonnifySettings__AllowedWebhookIpAddresses__0=35.242.133.146`

In the Monnify dashboard, set the Transaction Completion webhook to:

```text
https://api.moorehotelandsuites.com/api/payments/monnify-webhook
```

Production startup rejects a different Monnify host, disabled webhook source
filtering, missing credentials, or an invalid source-IP list. The webhook also
has a 256 KB body ceiling and per-source rate limit, requires exactly one valid
HMAC-SHA512 `monnify-signature`, ignores payment facts in the webhook body, and
performs a new server-to-server verification before changing the booking.

Monnify's sandbox does not add the production signature. This API therefore
does not accept sandbox webhooks. Local is provider-isolated and cannot perform
a real sandbox checkout or verification; automated tests may opt into in-memory
provider doubles without enabling network access. Never paste Monnify
credentials into source files, tickets, logs, or chat.

## Production requirements

Production startup fails closed when any of these are missing or unsafe:

- verified-TLS PostgreSQL with a least-privileged application user;
- a separate schema-owning migration credential and matching
  `DATABASE_RUNTIME_ROLE`; startup rejects runtime DDL, ownership, superuser,
  role-management, replication, database-creation and `BYPASSRLS` privileges;
- non-placeholder JWT, email, Cloudinary and bank-transfer settings;
- Monnify credentials and its strict webhook boundary when Monnify is enabled;
- a current pricing-quote requirement, truthful backup-mode and encrypted
  off-provider backup declarations, tested alert routing, and restore-drill evidence;
- Brevo and Cloudinary rotation/acceptance evidence, plus hosted-checkout,
  Monnify and PCI evidence before Monnify can be enabled;
- explicit HTTPS origins, hosts, public application URLs, and API URL;
- persistent Data Protection keys protected by the configured certificate;
- disabled Swagger, disabled automatic email confirmation, and trusted reverse
  proxy IP/network configuration.

On Render, use `ForwardedHeaders__TrustRenderEdge=true` with
`ForwardedHeaders__ForwardLimit=1`; Render's public service port is reachable
only through its edge, and only the edge-appended right-most forwarding hop is
consumed. On another host, disable this mode and configure the platform's exact
trusted proxy address or CIDR. Never use `0.0.0.0/0`.

Keep the API behind the configured reverse proxy, rotate credentials through the
cloud secret manager, and persist encrypted keys in Supabase using
`DataProtection__StorageProvider=Database`. Render Free hosts only the API;
run `./scripts/deploy-database.sh TESTED_IMAGE` externally before manually
deploying that release. Use encrypted logical backups and restore rehearsals. See
`PRODUCTION_DEPLOYMENT.md` and `render.yaml`.

## Verification commands

```bash
dotnet restore MooreHotels.sln
dotnet build MooreHotels.sln --configuration Release --no-restore
dotnet list MooreHotels.sln package --vulnerable --include-transitive
dotnet tool restore
dotnet tool run dotnet-ef migrations has-pending-model-changes \
  --project MooreHotels.Infrastructure \
  --startup-project MooreHotels.WebAPI
```
