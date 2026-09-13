# Production go-live plan — 12 September 2026

> Historical review, superseded by [the 13 September verification](RELEASE_VERIFICATION_2026_09_13.md).
> PR #7 was already merged on 9 September. Rechecking this working tree on
> 13 September reproduced three further code defects, so the original
> "entirely operational" conclusion below must not be used as release approval.

## Purpose and scope

This is a follow-up to `docs/PRODUCTION_READINESS_AUDIT.md` (7 September) and
`docs/LIVE_RELEASE_PROGRESS_2026_09_09.md` (9 September). It records an
independent code-level review performed on branch `codex/prod-hardening-1-5`
(the same branch backing draft PR #7), the fixes applied as a result, full
re-verification evidence, and a single sequenced checklist for the remaining
release-owner work. It does not repeat the infrastructure state already
recorded in the 9 September document. Both documents are dated snapshots;
recheck live infrastructure state before relying on either.

## What changed since the 7 September audit

An independent review (not the branch's own authors) traced the actual
request-handling paths for auth/MFA, payments/refunds, booking/pricing/
inventory, the HTTP pipeline, persistence/backups, and the email/media
outbox, rather than re-reading the prior audit's claims. Three issues were
confirmed severe enough to fix before merge, plus one latent correctness bug:

1. **MFA secrets were stored in plaintext.** `AspNetUserTokens` (TOTP
   authenticator keys, two-factor recovery codes) had no encryption —
   `AddDefaultTokenProviders()` was wired to the default EF Core store. A
   database read exposure would have meant a silent, total MFA bypass for
   every enrolled account. **Fixed**: `ProtectedUserStore` now encrypts every
   token value through the existing Data Protection stack. See
   "Sequencing note" below — this changes the order of an already-planned
   step.
2. **Refunds could be posted without a fresh, locked read of the guest
   credit balance or approval fields**, in `FolioService`/`BookingService`.
   Tracing the call path showed the two refund HTTP endpoints were already
   protected by `BookingsController.ExecuteBookingMutationAsync`'s row lock
   — so this was not exploitable through the current API surface — but
   `IFolioService.ApplyRefundAsync` had no such protection for any other
   caller (four integration tests already call it directly; so would any
   future endpoint or background job). **Fixed**: locking is now enforced
   inside `FolioService` itself via a new `ExecuteUnderBookingLockAsync`
   primitive, so correctness no longer depends on every caller remembering
   to wrap itself.
3. **A duplicate transactional email was possible on retry.** The Brevo
   idempotency key is sent as a custom SMTP header, not a documented
   server-side dedup mechanism. If the provider accepted a message but the
   subsequent outbox-row deletion failed, the retry would resend through
   Brevo. **Fixed**: the worker now records provider acceptance
   (`DeliveredAtUtc`) before attempting cleanup, so a cleanup failure no
   longer causes a resend.
4. **Latent timezone truncation bug** in booking/quote/inventory validation:
   `DateOnly.FromDateTime(booking.CheckIn)` truncated the UTC instant
   directly instead of converting to `Africa/Lagos` first (the correct
   pattern already existed in `OperationalReportingService`). Today's
   `Africa/Lagos`/14:00/12:00 configuration happens to mask it, but a future
   hours or timezone change would silently mis-validate quotes or
   miscalculate per-night inventory. **Fixed**: all three call sites now
   convert to hotel-local time first.

One item was investigated and found to be a documented design trade-off, not
a code defect: the Render edge-trust forwarded-headers configuration
(`TrustRenderEdge=true` with empty `KnownProxies`/`KnownNetworks`) behaves
exactly as ASP.NET Core's forwarded-headers middleware is documented to
behave, and an existing test (`ForwardedHeadersConfigurationTests`) already
asserts this. Its safety depends on Render's network topology actually
preventing direct-to-container access — see item 3 in the checklist below.

Four low-severity/cosmetic items were identified and intentionally **not**
fixed in this pass (documented so they aren't rediscovered as surprises):
`BookingAddOn`/`ReservationRoom` foreign-key cascades are `Cascade` while
every other financial relationship is `Restrict` (neutralized today by the
runtime role's revoked `DELETE` on bookings); four dashboard/reporting
queries (`GetOccupiedRoomNightsAsync`, `GetAverageNightlyRateAsync`,
`GetCheckInsCountAsync`, `GetCheckOutsCountAsync`) use raw UTC `.Date`
truncation — internally self-consistent, reporting-only, not worth the risk
of a partial fix without auditing every caller's date-boundary convention;
inclusive-tax breakdown math is additive rather than compounded when
multiple inclusive tax rules exist (doesn't affect actual totals); a dead
`Notification.IsRead` column is never read.

## Re-verification evidence (this session)

Performed from a clean tree (`rm -rf */bin */obj`) against .NET SDK
10.0.400 and a disposable PostgreSQL 17 container — not the pinned SDK
already installed in CI, but the same version:

- Locked restore (`--locked-mode`): passed, confirming no dependency changed.
- Release build with `-warnaserror`: 0 warnings, 0 errors.
- `dotnet format --verify-no-changes`: no files need changes.
- `dotnet list package --vulnerable --include-transitive`: no vulnerable
  packages in any of the six projects.
- `dotnet-ef migrations has-pending-model-changes`: none — the one new
  migration (`AddEmailOutboxDeliveredMarker`) fully captures the model
  change.
- Unit tests: 19 passed, 0 failed.
- Integration tests: 268 passed, 0 failed (265 pre-existing + 3 new tests
  added to prove the fixes: a concurrent-refund race test, an
  authenticator-key encryption test, and an email-outbox no-duplicate-resend
  test — each written to fail against the pre-fix code, not just pass
  against the fix).
- Full migration chain (28 migrations including the new one) applied
  cleanly to an empty PostgreSQL 17 database.
- Production Docker image: builds cleanly, publishes the migration bundle,
  runs as non-root `app` (uid 1654), and correctly refuses to start with no
  production configuration supplied (fail-closed validation lists every
  missing setting rather than starting unsafely).

No commit was created. All changes are in the working tree on
`codex/prod-hardening-1-5`, ready for review.

## Sequencing note: MFA enrollment must happen after this merges

`PRODUCTION_DEPLOYMENT.md` section 7 ("Initial administrator") has staff
enroll MFA as part of first login. Because `ProtectedUserStore` changes how
`AspNetUserTokens` values are read, **do not enroll any administrator in MFA
before this fix is deployed**. If it's already been done against the
existing Supabase project (unlikely — `docs/LIVE_RELEASE_PROGRESS_2026_09_09.md`
confirms zero bookings and no persisted Data Protection keys as of 9
September, and admin creation was still pending), there's no migration
problem. Confirm no admin has completed MFA setup on the live Supabase
project before deploying; if one has, that account's stored authenticator
key/recovery codes will need to be reset (`ResetAuthenticatorKeyAsync` +
`GenerateNewTwoFactorRecoveryCodesAsync`) after this deploy, not merely
re-read.

## Sequenced checklist to production

Work these in order; later steps depend on earlier ones.

1. **Review and merge this diff.** Have a second engineer review the 14
   changed files and 2 new migration files (per the existing audit's
   standing requirement for a second-engineer review), paying particular
   attention to `FolioService.ExecuteUnderBookingLockAsync` and
   `ProtectedUserStore` since both touch financial and authentication
   invariants. Push to `codex/prod-hardening-1-5`, let PR #7's CI/CodeQL run
   complete (it was last green at 284 tests on 9 September; expect ~287
   now), then merge to `main`.
2. **Generate and review the idempotent migration script** for the one new
   migration before the next database release step:
   ```bash
   dotnet tool run dotnet-ef migrations script --idempotent \
     --project MooreHotels.Infrastructure \
     --startup-project MooreHotels.Infrastructure
   ```
   Apply it to the Supabase project as its own release step, per the
   existing "Database changes" process in `README.md` — do not enable
   `Database__ApplyMigrationsOnStartup` in Production.
3. **Confirm Render's actual network topology before relying on
   `TrustRenderEdge`.** Verify with Render (dashboard or support) that the
   deployed service is reachable only through their edge/load balancer, not
   by a direct container IP or an alternate port. If that can't be
   confirmed, obtain the actual inbound proxy addresses/ranges and header
   sanitization guarantees from Render before configuring explicit trust.
   Do not substitute published outbound/egress ranges: those describe service
   connections to external providers, not inbound proxies. See
   [Render outbound IP documentation](https://render.com/docs/outbound-ip-addresses).
4. **Complete the remaining items already tracked in
   `docs/LIVE_RELEASE_PROGRESS_2026_09_09.md`** (verify current status first
   — this is a 3-day-old snapshot):
   - Merge PR #7 to `main` so the daily encrypted backup workflow runs from
     the default branch (GitHub only schedules workflows there); manually
     dispatch it once and verify the artifact.
   - Complete Brevo and Cloudinary credential-rotation/acceptance evidence,
     and finalize approved privacy/terms versions, URLs, and retention
     configuration.
   - Deploy the merged commit, switch the Render health check path to
     `/health/ready`, verify public health, and rehearse restart recovery
     with the existing Data Protection certificate.
5. **Only after step 1 is deployed**, follow `PRODUCTION_DEPLOYMENT.md`
   section 7: seed the first administrator, sign in, change the password,
   and enroll MFA; repeat for a second, separately controlled administrator
   (required by the emergency-suspension workflow, which forbids
   self-suspension and requires another active MFA-enabled Admin). Store
   recovery codes securely. Disable `SeedAdmin` and remove seed passwords
   afterward.
6. **Work through `PRODUCTION_DEPLOYMENT.md` section 8 ("Deployment
   acceptance")** in full before pointing the guest website or staff
   dashboard at the deployed API. That checklist already covers booking,
   pricing/quotes, inventory, folio, cancellation/no-show, amendments,
   housekeeping/maintenance, night audits, guest CRM/merge, privacy/
   retention, media, and the manual-transfer/refund flow. Two items are
   directly exercised by today's fixes and worth calling out explicitly
   during that pass:
   - "Refunds at or above `FinancialControls__HighValueRefundThreshold` were
     approved and completed by two different active Admin/Manager
     accounts" — also try firing two refund-completion requests for the
     same booking back-to-back and confirm only one posts.
   - Enrolling and logging in with MFA should work end-to-end; there is no
     user-visible change from the encryption fix, but confirm it anyway
     since it touches every MFA code path.
7. **Keep Monnify disabled** (`MonnifySettings__Enabled=false`) until its
   own separately tracked sandbox/webhook/live-payment/refund/PCI evidence
   is complete — unaffected by this round of fixes, still gating.
8. **Go/no-go**: do not deploy the dashboard or guest website, and do not
   enable Monnify, until every item above and every existing item in
   `PRODUCTION_DEPLOYMENT.md` section 8 is checked off with real evidence,
   not just configuration presence.

## What "production ready" now means for this codebase

Code-wise, this is ready for the review-and-merge step above. Every
previously-identified high-severity code issue has a verified fix, the
existing 33-item hardening history from the 7 September audit stands
unchanged, and nothing in this pass found a reason to distrust that
document's other claims — CI, secrets hygiene, and test substance were all
independently re-checked and held up. What remains between here and a real
guest taking a booking is entirely the operational sequence above: merge,
migrate, provider acceptance, admin/MFA setup in the right order, and the
deployment-acceptance walkthrough — not further code changes, unless that
walkthrough surfaces something new.
