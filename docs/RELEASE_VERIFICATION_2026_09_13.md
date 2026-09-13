# Release verification — 13 September 2026

## Decision

**Not approved for guest traffic.** The 12 September report was partly stale
and overstated the completeness of the code fixes. This document supersedes
its release status. The additive email-outbox migration was applied to the
empty production database during this verification. No API deployment was
performed.

## Confirmed against GitHub

- [PR #7](https://github.com/Goldpurp/MooreHotelAndSuites-Api/pull/7) merged
  on 9 September at 18:09:59 UTC, with successful verify and CodeQL checks.
  Current `main` is `655f9ec447ef0a4a28434dcf647406dd367dc7f7`.
- The September 12 hardening work was still uncommitted on the old PR branch.
  Those changes require a new PR and CI run; PR #7's green checks do not cover them.
- Daily encrypted backup runs succeeded on September 10, 11, 12 and 13.
  [The September 13 run](https://github.com/Goldpurp/MooreHotelAndSuites-Api/actions/runs/34745252421)
  produced the unexpired artifact `production-backup-34745252421-1`
  (176,388 bytes). Artifact presence does not prove it can be restored.
- After applying the email-outbox migration, a second manual encrypted backup
  [run #6](https://github.com/Goldpurp/MooreHotelAndSuites-Api/actions/runs/34754802892)
  completed successfully in 40 seconds and produced one retained artifact. The
  September 9 isolated restore rehearsal remains the latest restore evidence;
  this newer artifact has not yet been restored.

## Defects reproduced and fixed in this pass

1. **A transaction was mistaken for a booking lock.** The new folio helper
   skipped `FOR UPDATE` and tracker refresh whenever any transaction existed.
   A regression test preloaded the same guest credit in two caller-owned
   transactions: both refunds posted, exceeding available credit. The helper
   now acquires the booking lock and refreshes state in either transaction mode.
2. **A consumed high-value approval could be reused.** A second refund with
   a different reference and the same amount could reuse the first approval
   while credit remained. Completion now rejects approvals consumed by an
   earlier completion. A matching replay remains idempotent.
3. **Email acceptance was saved after a cleanup failure, rather than before
   cleanup.** The worker now commits `DeliveredAtUtc` before attempting deletion.
   A regression test requires the marker at deletion time. A separate existing
   test verifies that a failed deletion can be retried without resending.

All three regression cases failed before these fixes. The concurrency control
without a caller-owned transaction passed, isolating the defective branch.
After the fixes, all 23 focused refund, inventory, email and MFA tests passed.

The email outbox still provides at-least-once delivery: a process crash after
provider acceptance but before the marker commits can cause another send.
The custom Brevo header alone is not evidence of provider-enforced deduplication.
This change prevents resends after a durable marker, not every duplicate email.

## Current verification evidence

The pinned .NET 10.0.400 SDK and a disposable PostgreSQL 17 container on
localhost were used, never the production database.

- Locked restore passed.
- Release build with warnings as errors passed: zero warnings/errors.
- All 19 unit tests and 271 integration tests passed; none skipped.
- Formatting verification and shell-script syntax checks passed.
- EF reports no pending model changes.
- NuGet vulnerability gate passed for all six projects, including transitive dependencies.
- Trivy 0.74.0 scanned the committed release snapshot and reported zero secret findings.
- Generated and reviewed the idempotent SQL for just
  `20260912174325_AddEmailOutboxDeliveredMarker`; it adds one nullable
  `timestamp with time zone` column and the EF history entry.
- Rehearsed the full previous schema on an empty PostgreSQL 17 database,
  then applied that exact SQL twice. Both runs passed; exactly one migration
  entry and the expected nullable column were present afterward.

Hosted CI must still verify the new commit, including its Linux/container,
CodeQL, secret scanning and migration/restore gates. Prior CI success applies
only to the older merged commit.

## Release gates in order

- [x] Recheck PR #7 and scheduled-backup workflow status.
- [x] Reproduce and fix the additional code defects above.
- [x] Finish local build, test, formatting, dependency and EF verification.
- [x] Commit the hardening work on `codex/go-live-verification-2026-09-13`.
- [ ] Push the branch and open the prepared draft PR. Automatic approval review
  rejected the push because explicit authorization to send the source changes
  to `Goldpurp/MooreHotelAndSuites-Api` was required. No push or PR creation occurred.
- [ ] Obtain independent review and green CI for the new PR, then merge.
- [x] Recheck the actual production EF migration history and schema. Before
  migration, a read-only query in project `azclpxuabsffjkuhqzga` confirmed 28
  migrations, zero bookings, zero administrators, zero MFA-enabled users, zero
  Identity-token rows, zero persisted Data Protection keys, and no
  `email_outbox.DeliveredAtUtc` column.
- [x] Apply the reviewed additive SQL in
  `docs/releases/20260913-email-outbox-delivery.sql` as an external database
  release step. A post-apply query confirmed migration
  `20260912174325_AddEmailOutboxDeliveredMarker`, the nullable
  `email_outbox.DeliveredAtUtc` column, and 29 total migrations. Startup
  migrations remain off.
- [ ] Verify Render's remaining settings, inbound
  network isolation and forwarded-header sanitization before relying on
  `ForwardedHeaders__TrustRenderEdge=true`.
- [ ] Complete Brevo/Cloudinary provider acceptance evidence and revoke the
  superseded credentials after the replacements pass deployed tests;
  finalize owner-approved privacy/terms versions, URLs and retention settings.
- [ ] Deploy the reviewed commit; set `/health/ready`; verify health, CORS,
  queue operation, alert routes and restart recovery with the same certificate.
- [ ] Verify existing administrator/MFA state, then provision two separately
  controlled administrators and enroll MFA with the protected token store deployed.
  If earlier plaintext MFA tokens exist, plan reset/re-enrollment before rollout.
  Remove seed passwords and disable seeding after setup.
- [ ] Run every item in `PRODUCTION_DEPLOYMENT.md` section 8 on the deployed
  release, recording the commit, date, operator and evidence for each result.
- [ ] Approve guest/staff frontend release only after these gates pass.

Keep Monnify disabled for a manual-transfer-only launch. Enabling Monnify
requires its own provider, webhook, payment/refund and PCI acceptance evidence;
those deferred checks do not prevent an otherwise accepted manual-transfer launch.

## Corrected Render guidance

Never populate inbound proxy trust with Render's published outbound IP ranges.
[Those ranges](https://render.com/docs/outbound-ip-addresses) describe service
connections to external providers and are shared regionally. They are useful
for a provider's outbound-client allowlist, not proof of inbound proxy identity.

[Render's private-network documentation](https://render.com/docs/private-network)
states that Free web services cannot receive private-network traffic. Paid
services can receive it, including direct instance-IP traffic. Confirm the
actual plan and routes, header overwrite/append behavior and any other proxy
in front of the service. If isolation cannot be established, obtain verified
inbound proxy addresses/ranges from Render; do not guess them or enable blanket
trust based solely on the service name. Reassess before changing plans.

Provider acceptance and approved policy records have been requested from the
release owner. Their existence and live configuration are not assumed.
On 13 September, Brevo showed the exact sender
`info@moorehotelandsuites.com` as verified and showed green DKIM and DMARC
status for `moorehotelandsuites.com`; no additional Namecheap DNS change was
indicated. A new one-year production SMTP key named
`moore-hotels-render-prod-2026-09-13` was created and saved to Render as
`EmailSettings__ApiPass` using Save only. A new active Cloudinary key with the
same production-specific name and the provider's `Master Admin` role was
created for cloud `dxryndnhl`; its key, secret and cloud name were saved to the
Moore API Render service using Save only. The prior Brevo and Cloudinary
credentials remain active until the new credentials pass deployed acceptance
tests, so rotation is not yet complete.
The intended public policy URLs both return HTTP 200 and render substantive
documents. The privacy notice says it was last updated on 13 July 2026; the
currently deployed terms say they took effect on 1 January 2024. The release
owner selected a full refund at least 24 hours before check-in and no refund
inside the final 24 hours or for a no-show. The API defaults and `render.yaml`
now snapshot that rule as reservation policy `2026-09-13`, and focused tests
cover both sides of the cutoff. The separate frontend working tree now states
the same rule with an effective date of 13 September 2026; it must be reviewed,
committed and deployed before that terms version is configured in the API.
Its 10 automated tests and TypeScript check pass. Its production build currently
stops during required sitemap generation because the configured production room
catalogue API times out. Deploy and verify the API before producing the final
frontend build; do not weaken the production sitemap requirement to bypass this.
Render browser inspections repeatedly timed out before a usable service state
was returned, so the active deployment and current settings remain unverified.
Direct HTTPS checks of `/health/live`, `/health/ready`, and `/api/health` on
`api.moorehotelandsuites.com` also timed out after 45 seconds with no response
and no HTTP status. This proves the public API was not usable during the check;
it does not distinguish a sleeping Free service from a failed deployment,
custom-domain routing problem, or another Render-side issue.

The Render Deploys page confirms service `srv-d5uhns4oud1c73bn28o0` is on the
Free plan and still marks old commit `3e901a1` as Live. A manual deployment of
merged PR #7 commit `655f9ec` failed after 1 minute 44 seconds. Its event page
reports exit status 139 while starting the application. The actionable log is
the fail-closed production configuration validation: encrypted off-provider
backups and current restore-drill evidence were unset; all four required alert
categories and their routing evidence were unset; Brevo and Cloudinary
credential-rotation, acceptance-time and evidence references were unset; and
the required privacy/terms acceptance, retention worker, approved versions and
public HTTPS URLs were unset. These values must represent completed work; do
not use placeholder evidence merely to make startup pass. The newer code and
health routes therefore have not been deployed, regardless of the service-level
Live badge.
