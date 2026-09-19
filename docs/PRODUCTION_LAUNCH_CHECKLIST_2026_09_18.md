# Production launch checklist — 18 September 2026

This checklist is the release record for opening Moore Hotels & Suites to guest
traffic. A declaration may be enabled in Render only after the linked evidence
exists. Placeholder evidence must never be used to make startup validation pass.

## Completed

- [x] API release `5316a05` deployed and `/health/ready` returns HTTP 200 with
  the production database connected.
- [x] Dashboard release `36a8d6a` deployed. It preserves the API's saved room
  category and floor values in the editor; PR #14 passed all three required
  checks before merge.
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
- [x] Default-branch release gate run `35408787862` passed on 19 September
  2026: formatting, build, 21 unit tests, 293 integration tests, analyzers,
  dependency and migration checks, database-boundary rehearsals, restore,
  container, secret scans and CodeQL.
- [x] The guest app and admin dashboard each passed their 12-test frontend
  suite and TypeScript typecheck against the launch release.

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
- [x] Set `OperationalReadiness__EncryptedOffProviderBackupsEnabled=true`,
  `OperationalReadiness__LastRestoreDrillAtUtc`, and
  `OperationalReadiness__RestoreDrillEvidenceReference` from that evidence in
  the Moore API Render service.

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
- [x] Added free UptimeRobot monitor `803981994` for `/health/operations`,
  routed alerts to `moorehotelsandsuites@gmail.com`, and confirmed both DOWN
  and UP test notifications on 18 September 2026.
- [x] Enabled the free GitHub synthetic monitor on the default branch. It checks
  the sanitized operational response and samples p95 latency against the
  two-second production threshold every ten minutes. Manual run `35337786213`
  passed with p95 `0.419794s`.
- [x] Recorded the monitor/test reference and enabled the four
  `OperationalReadiness__*AlertsEnabled` declarations plus
  `OperationalReadiness__AlertRoutingEvidenceReference` in Render. Deploy
  `dep-damitptbedkc73bu7ki0` then succeeded and both readiness endpoints stayed
  HTTP 200.

## Gate 3 — Brevo acceptance

- [x] Verified the exact sender `info@moorehotelandsuites.com` and authenticated
  `moorehotelandsuites.com` domain remain active in Brevo on 18 September 2026.
- [x] Replaced the unused malformed Brevo credential with
  `Moore Render Production 2026-09-18`, configured both API credential settings
  in Render, and released deploy `dep-damk88u1egvs73cit9sg`.
- [x] Delivered a production password-reset message and a booking
  email-verification message to `moorehotelsandsuites@gmail.com` at 15:03 WAT
  on 18 September 2026. Brevo records both `Sent` and `Delivered` events and the
  mailbox received both messages. No message link was opened.
- [x] Confirmed the replacement credential is active, expires on 18 September
  2027, and its provider record shows production use on 18 September 2026.
- [x] Delivered representative booking and cancellation messages for production
  test reservation `MHS675472` using the replacement credential on 18 September
  2026. The reservation held Room 001 inventory, cancellation restored it, and
  both outbox entries cleared with zero exhausted messages.
- [x] Delivered password-reset and secure-link messages using the replacement
  production credential. After revoking the old key, a fresh authenticated
  booking-verification request returned HTTP 202 and its outbox entry cleared
  from one pending message to zero with no exhausted messages.
- [x] Delivered the automatic payment-expiry message for production reservation
  `MHS843813` at 01:45 WAT on 19 September 2026 using the replacement
  credential. The worker cancelled the unpaid reservation, balanced its folio,
  restored Room 001 availability, cleared the email queue with zero exhausted
  messages, and the destination mailbox received the cancellation notice.
- [x] Confirm matching accepted/delivered events and receipt in the destination
  mailbox for the two non-booking acceptance messages without exposing secrets.
- [x] Verified retry and exhausted-message behavior when the prior malformed
  credential was rejected; delivery recovered after the replacement credential
  was deployed and fresh messages were queued.
- [x] Deactivated the superseded Brevo API key `Moore Hotel` on 18 September
  2026. Brevo shows it as `Deactivated`; the replacement key
  `Moore Render Production 2026-09-18` remains active and delivered a fresh
  secure-link message after the revocation.
- [x] Saved the rotation reference, acceptance timestamp and delivery-test
  reference in the three `ProviderAcceptance__Brevo__*` Render variables on
  19 September 2026. Their activation is verified in Gate 5 with the launch
  deployment.

## Gate 4 — Cloudinary acceptance

- [x] Verified against production on 18 September 2026 that a valid PNG uploads
  and persists, an 8,388,609-byte image is rejected before submission, and a
  file named `.png` with invalid contents is rejected by the API's magic-byte
  validation.
- [x] Added a temporary third image to room `001`, confirmed it persisted after
  reopening the editor, removed only that test image, and confirmed the room
  returned to its two original images. The public operations probe reported the
  media-deletion queue `Operational` after the deletion worker processed it.
  `DataAndImageUpdateTests.Room_update_replaces_all_images_clears_amenities_and_honours_offline_state`
  also verifies that the old managed asset remains until the database update
  commits and is then removed by the worker.
- [x] Verified deletion durability, retry and idempotency on 18 September 2026.
  Room-image replacement and general-media integration tests prove that the
  database detach and deletion job commit together before provider deletion.
  `MediaDeletionWorker` leases jobs, retries failures with bounded exponential
  backoff for up to 12 attempts, and removes a job only after provider success;
  Cloudinary treats both `ok` and `not found` as successful deletion. Public IDs
  remain server-side and are not accepted as room-image ownership evidence.
- [x] Disabled the superseded Cloudinary `Root` credential on 18 September
  2026; the replacement `moore-hotels-render-prod-2026-09-13` credential
  remains active.
- [x] Recorded the rotation reference, acceptance timestamp
  (`2026-09-18T16:19:58Z`) and this lifecycle-test reference in the three
  `ProviderAcceptance__Cloudinary__*` variables. Render deployment
  `dep-damm9pgu01pc73aq8tig` succeeded and became live.

## Gate 5 — staged production opening

- [x] Deployed the accepted Render configuration with
  `LaunchGate__Enabled=true`; deployment `dep-damm9pgu01pc73aq8tig` started in
  Production and passed Render's `/health/ready` check. The live readiness
  probe returned `Ready` with the database connected, and the operations probe
  returned `Operational` for the database, email queue, media-deletion queue
  and payments.
- [x] Published room `001` (`James`). It is `Available`, uses active room type
  `DELUXE`, has capacity 2 and price NGN 35,000, and appears in the live admin
  room ledger with its production images.
- [x] Set `LaunchGate__Enabled=false`. Render deployment
  `dep-damtr0egekts73evft40` deployed verified main commit `85dcb69` and became
  live on 19 September 2026.
- [x] Confirmed anonymous production access returns HTTP 200 for Room 001,
  room-type search, Room 001 availability, the current privacy/booking-policy
  metadata and a one-night Deluxe pricing quote for NGN 35,000.
- [x] Confirmed with production reservation `MHS675472` that anonymous booking
  requires a valid single-use email-verification token and direct transfer
  creates the reservation and folio once. The reservation held Room 001,
  delivered guest/admin messages, and cancellation restored inventory.
- [x] Changed the guest deployment to `SITEMAP_REQUIRE_ROOMS_API=true` and
  rebuilt successfully as `dep-damtt9qjnfac73ekvsbg`. The build generated and
  verified ten canonical URLs including the published Room 001 detail URL;
  the live sitemap and room-detail page expose that room and its booking rate.

## Gate 6 — final acceptance

- [x] Verified trusted and untrusted CORS origins on 18 September 2026. The
  production admin origin received `Access-Control-Allow-Origin`; an untrusted
  origin did not, including for preflight requests.
- [ ] Verify Admin, Manager, department Staff and Client permissions.
- [ ] Verify room creation/update/image replacement and offline/online behavior.
- [ ] Verify free and late cancellation policy boundaries and no-show handling.
- [ ] Verify privacy acceptance, export/request flows and audit records.
- [ ] Verify dashboard and guest flows on desktop and mobile widths.
- [x] Confirmed `/health/live`, `/health/ready` and `/health/operations` remain
  green after the final API restart. Readiness reports the database connected;
  operations reports the database, email queue, media-deletion queue and
  payments operational. UptimeRobot alert delivery to the release owner was
  previously exercised and both production monitors are green.

## Explicitly deferred by the release owner

- [ ] Rotate the administrator password from the live dashboard.
- [ ] Enroll authenticator-app TOTP MFA by scanning the QR code and store the
  recovery codes offline. Require MFA for every staff account with access to
  guest, booking, payment or administrative data.
