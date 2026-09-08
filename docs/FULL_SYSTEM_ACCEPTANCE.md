# Moore Hotels & Suites - Full System Production Acceptance

This document records the acceptance criteria, verification results, and operational runbooks for production hardening items 6 through 10 of the Moore Hotels & Suites API.

---

## 1. Item 6: Personal-Data Retention & Legal Hold Acceptance

### 1.1 Personal Data Inventory & Scrubbing Matrix
Every personal data field across the core domain entities is inventoried, scrubbed upon retention expiry or erasure, and protected against unauthorized retention:

| Entity | Field | Retention Action | Post-Retention State |
| :--- | :--- | :--- | :--- |
| `Guest` | `FirstName`, `LastName` | Anonymized | `"Former"`, `"Guest"` |
| `Guest` | `Email`, `NormalizedEmail` | Anonymized | `"anonymized-{guestId}@privacy.invalid"` |
| `Guest` | `Phone`, `NormalizedPhone` | Scrubbed | `"REDACTED"`, `""` |
| `Guest` | `PreferencesJson` | Reset | `"{}"` |
| `Guest` | `AvatarUrl` | Nullified | `null` |
| `Guest` | `AnonymizedAtUtc` | Timestamped | Current UTC timestamp |
| `ApplicationUser` | Names, email/usernames, phone and avatar | Anonymized | Non-identifying placeholders/nulls |
| `ApplicationUser` | Password, MFA, external logins, claims and tokens | Removed/revoked | `null`/disabled/deleted with a rotated security stamp |
| `ApplicationUser` | Activity/status/anonymization timestamps | Lifecycle markers | Account eligibility and erasure remain attributable |
| `Booking` | `Notes` | Scrubbed | `null` |
| `Booking` | `RefundNotes` | Scrubbed | `null` |
| `Booking` | `GuestAccessTokenHash` | Scrubbed | `null` |
| `BookingAddOn`| `Notes` | Scrubbed | `null` |
| `VisitRecord` | `GuestName` | Scrubbed | `"Former Guest"` |
| `GuestNote` | `Body`, `IsSensitive` | Sanitized | `"Removed by retention policy"`, `false` |
| `PrivacyRequest` | Free-text details/notes and evidence references | Sanitized after subject retention | `null`/non-identifying markers |
| `Notification`| `Title`, `Message` | Sanitized | `"Archived booking activity"`, `"Archived booking activity."` |
| `EmailOutbox` | Subject-linked queued mail | Deleted with the retained subject | No queued copy remains |
| `EmailOutbox` | Exhausted recipient/payload | Quarantined | `"redacted@delivery-failure.invalid"`, `"{}"` |
| `EmailOutbox` | `QuarantinedAtUtc`, `DeliveryFailureMetadataJson` | Quarantined | UTC timestamp, minimal failure metadata JSON |
| `AuditLog` | PII, secrets and free-text JSON properties | Redacted at every `SaveChanges` boundary | `"[REDACTED]"` while operational identifiers/states remain |

Retention eligibility requires the guest's most recent stay to exceed
`GuestRetentionDays`. Unlinked guests are then eligible; a linked `Client`
account additionally must exceed `InactiveAccountRetentionDays` based on its
latest authentication or status activity. Staff-linked guests, active privacy
requests and legal holds are excluded. Existing active clients receive a
conservative activity marker during upgrade so deployment cannot immediately
erase an account with unknown historical activity.

### 1.2 Legal Hold Protections
- **Entity State**: `IsUnderLegalHold`, `LegalHoldPlacedAtUtc`, `LegalHoldReason`, `LegalHoldPlacedByUserId` on `Guest`.
- **Retention Worker Exemption**: Guests with `IsUnderLegalHold = true` or active legal hold timestamps are skipped by `PrivacyRetentionWorker.SweepOnceAsync`.
- **Erasure Rejection**: Requests to `DELETE /api/privacy/guests/{id}/erasure` for a guest under legal hold are rejected with `400 Bad Request`.
- **Administrative Control**: Only `Admin` users can place (`POST /api/privacy/guests/{id}/legal-hold`) or release (`DELETE /api/privacy/guests/{id}/legal-hold`) legal holds with mandatory reason logging in audit trails.

### 1.3 Outbox Failure Quarantine
- Emails that reach the maximum attempt limit (12 attempts) or are processed during retention sweeps are flagged with `QuarantinedAtUtc`.
- Recipient email address is scrubbed to `redacted@delivery-failure.invalid` and protected payload to `"{}"`.
- Minimal non-PII delivery failure metadata (timestamp, attempt count, last exception type/truncated message) is retained in `DeliveryFailureMetadataJson`.
- Quarantined emails are excluded from future worker sweeps via `"QuarantinedAtUtc" IS NULL` in `SELECT FOR UPDATE SKIP LOCKED`.

---

## 2. Item 7: Durable Confirmations & Folio Receipts Acceptance

### 2.1 Reservation Amendment Confirmations
- **Atomic Transaction**: When a reservation is amended via `IReservationAmendmentService.AmendAsync`:
  1. Stay dates, room categories, and occupancy are validated against inventory with `pg_advisory_xact_lock`.
  2. The amendment quote is verified and consumed.
  3. Previous room charge entries on the folio are voided and quoted pricing entries are added.
  4. Physical rooms and reservation rooms are reallocated.
  5. An immutable `BookingAmendment` record and audit log are created.
  6. A `BookingAmendmentConfirmation` email outbox message is generated with a deterministic ID (`amendment:{amendment.Id:N}`) and added to the context.
  7. All database changes and email queuing commit in the same database transaction.
- **Email Payload**:
  - `GuestName`, `BookingCode`, `RoomTypeName`, `RoomQuantity`, `CheckIn`, `CheckOut`, `Nights`, `RoomSubtotal`, `TaxAmount`, `FeeAmount`, `TotalAmount`, `BalanceDue`, `PriceDifference`, `ManageBookingUrl`, `AmendmentReason`.

### 2.2 Folio Settlement Receipts
- **Atomic Transaction & Idempotency**:
  - `FolioService.PostPaymentAsync`: Posts a debit/credit folio entry and queues a `FolioReceipt` email with template `FolioReceipt`, entry type `"Payment"`, method, amount, and remaining balance.
  - `FolioService.PostCreditAsync`: Posts a credit entry and queues a `FolioReceipt` email with entry type `"Credit"`.
  - `FolioService.ApplyRefundAsync`: Posts a refund debit entry and queues a `FolioReceipt` email with entry type `"Refund"`.
  - Every receipt email has a deterministic ID (`folio-receipt:{entry.Id:N}`), guaranteeing that retrying a payment, credit, or refund with the same idempotency key will never generate duplicate receipt emails.

---

## 3. Item 8: Staff Role Authorization & Least Privilege Matrix

The Moore Hotels & Suites API enforces strict separation of duties across 8 operational roles and departments:

| Endpoint | Method | Admin | Manager | Finance | Cashier | Reception | Concierge | Housekeeping | Engineering |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| `/api/folios/{code}` | GET | **200** | **200** | **200** | **200** | **200** | **403** | **403** | **403** |
| `/api/folios/{code}/payments` | POST | **200** | **200** | **200** | **200** | **403** | **403** | **403** | **403** |
| `/api/folios/{code}/charges` | POST | **200** | **200** | **200** | **200** | **200**¹ | **403** | **403** | **403** |
| `/api/folios/{code}/credits` | POST | **200** | **200** | **403** | **403** | **403** | **403** | **403** | **403** |
| `/api/folios/{code}/close` | POST | **200** | **200** | **200** | **200** | **403** | **403** | **403** | **403** |
| `/api/guest-crm/{id}` | GET | **200** | **200** | **403** | **403** | **200**²| **403** | **403** | **403** |
| `/api/guest-crm/{id}/notes` | POST | **200** | **200** | **403** | **403** | **403** | **403** | **403** | **403** |
| `/api/reservation-operations/{id}/room-assignment` | POST | **200** | **200** | **403** | **403** | **200** | **403** | **403** | **403** |
| `/api/reservation-operations/{id}/amend` | POST | **200** | **200** | **403** | **403** | **200** | **403** | **403** | **403** |
| `/api/housekeeping/tasks` | GET/POST | **200** | **200** | **403** | **403** | **403** | **403** | **200** | **403** |
| `/api/maintenance/work-orders` | GET/POST | **200** | **200** | **403** | **403** | **403** | **403** | **403** | **200** |
| `/api/pricing/configuration` | GET | **200** | **200** | **403** | **403** | **403** | **403** | **403** | **403** |
| `/api/pricing/rate-plans` | POST | **200** | **200** | **403** | **403** | **403** | **403** | **403** | **403** |
| `/api/channels` | GET/POST | **200** | **200** | **403** | **403** | **403** | **403** | **403** | **403** |
| `/api/privacy/guests/{id}/legal-hold` | POST/DELETE | **200** | **403** | **403** | **403** | **403** | **403** | **403** | **403** |
| `/api/privacy/guests/{id}/erasure` | DELETE | **200** | **403** | **403** | **403** | **403** | **403** | **403** | **403** |

*Notes:*
- ¹ *Reception staff may only post itemized add-on charges; general financial debit adjustments throw unauthorized.*
- ² *Reception staff can view guest CRM profiles, but management-only sensitive notes are filtered out.*

---

## 4. Item 9: Frontend Integration & End-to-End Acceptance

The frontend client repositories (Guest Web App and Staff Dashboard) must adhere to the following specifications:

### 4.1 Guest Web Application Acceptance
1. **Email Verification Link Flow**:
   - The booking verification token is passed via URL hash fragment: `/book#bookingVerificationToken=...`; the guest enters the matching email in the booking form.
   - Application extracts fragment using `URLSearchParams(window.location.hash.substring(1))` and calls `history.replaceState` to eliminate tokens from browser history, logs, and referrer headers.
   - Submits token in `POST /api/bookings` as `emailVerificationToken`.
   - Account verification calls `POST /api/auth/verify-email` with `userId` and `token` in JSON. Password reset calls `POST /api/auth/reset-password` with `userId`, `token`, and the new passwords. Tokens are never sent to an API query string.
2. **Pricing Quotes & Immutability**:
   - `POST /api/pricing/quotes` must be invoked to obtain `quoteId` and `quoteToken`.
   - Display room subtotal, discount, included taxes, exclusive taxes, fees, and total.
   - Quote token held strictly in-memory; never stored in `localStorage` or session cookies.
3. **Guest Access Links**:
   - Guest booking management link contains hash fragment `#accessToken=...`.
   - Guest site extracts the token, calls `POST /api/bookings/lookup` with the code in JSON, and sends the token only in `X-Booking-Access-Token`.
   - Guest email and staff cancellation reasons are never sent in query strings.
   - Code + email alone is rejected for booking lookup.
4. **Direct Bank Transfer Settlement**:
   - Booking checkout shows hotel bank accounts.
   - Instructions state payment verification requires staff review; status shows `AwaitingVerification`.
   - Monnify card checkout is enabled only when provider activation flags are set.

### 4.2 Staff Dashboard Application Acceptance
1. **Mandatory MFA Enrollment**:
   - Upon initial staff login, `mfaSetupRequired = true` prompts mandatory setup.
   - Call `POST /api/mfa/setup` -> render QR code (`authenticatorUri`) and key (`sharedKey`).
   - Confirm code via `POST /api/mfa/enable` -> present emergency recovery codes.
2. **SignalR Active Revocation**:
   - Dashboard sends its JWT in the header of `POST /api/notifications/realtime-ticket`, then connects to `/hubs/notifications` with the returned one-minute, single-use ticket using WebSockets and `skipNegotiation: true`.
   - A fresh ticket is required for each reconnect; the JWT itself is never placed in `access_token`.
   - Listens for `AccessRevoked` event -> immediately purges in-memory tokens and redirects to login.
3. **Emergency Administrator Control**:
   - Ordinary status routes reject Administrator targets. Emergency suspend/reactivate requires another active MFA-enabled Admin to re-enter their password and current TOTP code, provide a reason, and type the target-specific confirmation.
   - Self-suspension is rejected. Concurrent mutual suspension preserves exactly one operational Admin. Suspension rotates the target security stamp, disconnects active realtime sessions, sends an account notice, and records an immutable audit event.
3. **Reservation Amendment Workflow**:
   - Request amendment quote via `POST /api/pricing/quotes/amendments`.
   - Show side-by-side repricing differences.
   - Submit amendment via `POST /api/reservation-operations/{id}/amend`.
4. **Folio Management & Receipting**:
   - Cashiers and Finance staff post payments and print/email folio receipts.
   - Idempotency key generated as UUID and reused upon connectivity timeout.
5. **Privacy & Legal Hold UI**:
   - Staff viewing guest profiles see a persistent legal hold badge if `isUnderLegalHold` is true.
   - Admin tools include "Place Legal Hold" modal and "Release Legal Hold" confirmation.

---

## 5. Item 10: Production Infrastructure & Operational Acceptance

### 5.1 Database Backup & Restore Drill Runbook
- **Engine**: PostgreSQL 16+ on managed infrastructure.
- **Automated Backup Strategy**:
  - Daily full backup via `pg_dump` with custom compressed format (`-Fc`).
  - Continuous WAL archiving for Point-In-Time Recovery (PITR) up to 30 days.
- **Quarterly Restore Drill Verification** (and after a material database or
  provider change):
  1. Restore a chosen recovery point to a new isolated database whose name
     contains `restore`, `drill`, or `rehearsal`; never overwrite production.
  2. Run
     `RESTORE_DRILL_CONNECTION_STRING='...' ./scripts/verify-restore-drill.sh`.
     The verifier checks required tables, the production environment marker,
     release migration and deployment invariants. It derives the expected
     migration from this checkout; when running without migration sources,
     explicitly set `RESTORE_DRILL_EXPECTED_MIGRATION` to the intended release.
  3. Compare booking, guest, room, payment-ledger and audit-log counts plus the
     newest booking timestamp with the source manifest.
  4. Rehearse connection cutover and rollback without allowing the copy to send
     email or call payment/media providers.
  5. Store approved evidence outside the repository, update the operational
     readiness timestamp/reference, and remove the isolated database.

### 5.2 External Provider Gating & Outage Fallback
- **Transactional Email (Brevo)**:
  - Validated via `POST https://api.brevo.com/v3/smtp/email`.
  - Transient provider errors trigger exponential backoff retry up to 12 attempts.
  - Hard provider failures or exhausted retries trigger secure quarantine (`QuarantinedAtUtc`).
  - Fallback: Local capture mode enabled when `EmailSettings__DeliveryMode = "Capture"` for staging/development.
- **Media Asset Storage (Cloudinary)**:
  - Deletion jobs use durable outbox (`media_deletion_jobs`).
  - Transient Cloudinary outages do not block database image detachments.
  - Failed deletions surface at `GET /api/admin/media-deletions/failed` with retry controls.

### 5.3 Migration Reconciliation
- The 26-migration chain is current through
  `20260907113949_BindAmendmentQuotesToReservations`; full empty-database
  up/down/up rehearsal passes.
- The final hardening sequence also includes
  `20260906091420_CompleteProductionHardeningSixToTen`,
  `20260907070112_LinkEmailOutboxToDataSubjects`, and
  `20260907084720_CompleteAccountRetentionAndAuditMinimization`.
- Schema changes verified:
  - `guests`: Added `IsUnderLegalHold`, `LegalHoldPlacedAtUtc`, `LegalHoldReason`, `LegalHoldPlacedByUserId`.
  - `email_outbox`: Added `QuarantinedAtUtc`, `DeliveryFailureMetadataJson`.
  - Indexes: `IX_guests_IsUnderLegalHold_LegalHoldPlacedAtUtc`, `IX_email_outbox_QuarantinedAtUtc`.

### 5.4 Feature Boundaries for Initial Launch
The following capabilities are deliberately disabled or restricted for initial production deployment:
1. **Monnify Auto-Settlement**: Monnify webhooks require explicit signature verification and are disabled until production merchant keys and IP allowlists are provisioned. Primary guest payment path is direct bank transfer.
2. **External OTA Sync**: Channel management (`/api/channels`) endpoints are administrative only. Direct two-way inventory sync with Booking.com / Expedia is disabled for launch.
3. **Bulk Marketing Communications**: The outbox worker processes transactional emails only (`TransactionalEmailTemplates`). Marketing campaigns are prohibited.
4. **Single-Node Execution**: The first release remains one API instance because
   realtime tickets, connection revocation and SignalR broadcasts are
   process-local. Durable workers already use database row locks, leases and
   idempotent state transitions; multi-instance deployment still requires a
   tested shared SignalR backplane, ticket store and revocation channel.
