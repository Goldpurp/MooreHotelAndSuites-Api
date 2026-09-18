# Production launch checklist — 18 September 2026

This checklist is the release record for opening Moore Hotels & Suites to guest
traffic. A declaration may be enabled in Render only after the linked evidence
exists. Placeholder evidence must never be used to make startup validation pass.

## Completed

- [x] API release `32e5926` deployed and `/health/ready` returns HTTP 200 with
  the production database connected.
- [x] Dashboard release `c69a190` deployed.
- [x] Guest release `b85282f` deployed.
- [x] Production schema contains 29 EF migrations, ending at
  `20260912174325_AddEmailOutboxDeliveredMarker`.
- [x] `email_outbox.DeliveredAtUtc` exists.
- [x] One production administrator exists.
- [x] Reservation policy `2026-09-13` represents a full refund at least 24
  hours before check-in and no refund inside the final 24 hours or for a no-show.
- [x] Monnify remains disabled for the direct-transfer-only launch.
- [x] Production privacy and booking-terms configuration passes API startup
  validation.

## Gate 1 — recovery evidence

- [x] Encrypted off-provider backup run
  [`35319130558`](https://github.com/Goldpurp/MooreHotelAndSuites-Api/actions/runs/35319130558)
  succeeded on 18 September 2026. Artifact
  `production-backup-35319130558-1` is 181,009 bytes, has SHA-256 digest
  `ff5f72382daecf450f76466e66ec28f561430fc92a5d793009dbf7a37cfd7f9a`,
  and is retained through 18 October 2026.
- [x] The 9 September 2026 isolated PostgreSQL 17 restore drill passed the
  48-table source/restore manifest comparison. Evidence:
  `docs/LIVE_RELEASE_PROGRESS_2026_09_09.md#production-backup-restore-rehearsal-completed`.
- [ ] Set `OperationalReadiness__EncryptedOffProviderBackupsEnabled=true`,
  `OperationalReadiness__LastRestoreDrillAtUtc`, and
  `OperationalReadiness__RestoreDrillEvidenceReference` only from that evidence.

## Gate 2 — free operational monitoring

- [x] Implement a sanitized anonymous `/health/operations` endpoint. It returns
  HTTP 200 only when the database, email queue, media-deletion queue and payment
  anomaly checks are operational; it returns HTTP 503 otherwise.
- [x] Permit GET and HEAD probes through the pre-launch middleware.
- [x] Add access-control, launch-gate, HEAD and degraded-queue integration tests.
- [x] Run the complete local API test suite: 293 integration tests and 21 unit
  tests passed on 18 September 2026. CI must repeat these checks on the PR.
- [x] Deploy the endpoint while the launch gate remains enabled. Render deploy
  `dep-damh33ad0e5s73fe0rn0` released merge commit `5316a05` on 18 September
  2026; `/health/operations` returned HTTP 200 with every check operational.
- [ ] Add a free UptimeRobot monitor for `/health/operations`, route alerts to
  the release-owner mailbox and perform a test notification.
- [ ] Enable the free GitHub synthetic monitor on the default branch. It checks
  the sanitized operational response and samples p95 latency against the
  two-second production threshold every ten minutes.
- [ ] Record the monitor/test reference and then enable the four
  `OperationalReadiness__*AlertsEnabled` declarations plus
  `OperationalReadiness__AlertRoutingEvidenceReference`.

## Gate 3 — Brevo acceptance

- [ ] Verify the exact sender and domain authentication remain active.
- [ ] Deliver representative booking, cancellation, expiry, password-reset and
  secure-link messages using the replacement production credential.
- [ ] Confirm matching accepted/delivered events and receipt in the destination
  mailbox without secrets or unexpected personal data.
- [ ] Verify retry and exhausted-message recovery behavior.
- [ ] Revoke the superseded credential and confirm it fails.
- [ ] Record the rotation reference, acceptance timestamp and delivery-test
  reference in the three `ProviderAcceptance__Brevo__*` variables.

## Gate 4 — Cloudinary acceptance

- [ ] Verify allowed room images upload and invalid/oversized content is rejected.
- [ ] Replace a room image and confirm the old managed asset is queued and deleted
  only after the database update commits.
- [ ] Verify deletion retry/idempotency and provider identifiers remain server-side.
- [ ] Revoke the superseded credential and confirm it fails.
- [ ] Record the rotation reference, acceptance timestamp and lifecycle-test
  reference in the three `ProviderAcceptance__Cloudinary__*` variables.

## Gate 5 — staged production opening

- [ ] Deploy the accepted Render configuration with
  `LaunchGate__Enabled=true`; confirm startup, readiness and operations probes.
- [ ] Publish room `001` (`James`). It is currently `Available`, has active room
  type `DELUXE`, capacity 2 and price NGN 35,000, but `IsOnline=false`.
- [ ] Set `LaunchGate__Enabled=false` and redeploy.
- [ ] Confirm public rooms, quotes, availability and policies return HTTP 200.
- [ ] Confirm anonymous booking requires a valid single-use email-verification
  token and direct-transfer booking creates the reservation and folio once.
- [ ] Change the guest deployment to `SITEMAP_REQUIRE_ROOMS_API=true`, rebuild
  and confirm the live sitemap contains the published room.

## Gate 6 — final acceptance

- [ ] Verify trusted and untrusted CORS origins.
- [ ] Verify Admin, Manager, department Staff and Client permissions.
- [ ] Verify room creation/update/image replacement and offline/online behavior.
- [ ] Verify free and late cancellation policy boundaries and no-show handling.
- [ ] Verify privacy acceptance, export/request flows and audit records.
- [ ] Verify dashboard and guest flows on desktop and mobile widths.
- [ ] Confirm `/health/live`, `/health/ready` and `/health/operations` remain
  green after a restart and that an alert test reaches the release owner.

## Explicitly deferred by the release owner

- [ ] Rotate the administrator password from the live dashboard.
- [ ] Enroll authenticator-app TOTP MFA by scanning the QR code and store the
  recovery codes offline. Require MFA for every staff account with access to
  guest, booking, payment or administrative data.
