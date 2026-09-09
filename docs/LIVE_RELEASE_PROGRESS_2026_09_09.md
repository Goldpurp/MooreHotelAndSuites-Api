# Live release progress — 9 September 2026

## Verified

- GitHub's `Goldpurp/MooreHotelAndSuitesProduction-api` URL redirects to
  `Goldpurp/MooreHotelAndSuites-Api`. These are the same public repository.
  Its remote `main` and Render's last successful deployment are `3e901a1`.
- The tested release is local branch `codex/prod-hardening-1-5`. Draft PR #7 is
  open against `main`; its latest complete CI/CodeQL run passed 284 tests and
  reported zero open CodeQL alerts. A fresh scan of the Git archive reported
  zero secret findings.
- Live read-only queries against the selected Ireland Supabase project
  `azclpxuabsffjkuhqzga` confirmed 28 EF migrations, 48 public tables, zero
  bookings, and zero persisted Data Protection keys.
- `moore_runtime` has no superuser, role-creation, database-creation or
  BYPASSRLS privileges. The saved runtime credentials remain outside Git.
  The complete `validate-runtime-database-role.sh` check also passed live.
- The privately saved Render PFX passes password, matching-private-key and
  expiry checks. It expires on 25 October 2028. No key was replaced.

## Render changes completed

- Changed Auto-Deploy from **After CI Checks Pass** to **Off** and verified
  the selected value. Database migration and API deployment remain serial.
- Added and saved 12 nonsecret configuration values using **Save only**:
  database retry/timeout/context-pool defaults; NGN pricing, 15-minute quotes
  and mandatory quote validation; daily-backup RPO and four-hour RTO targets;
  restore-evidence maximum age; queue/payment warning thresholds; and the
  disabled hosted-payment acceptance declaration.
- Encryption-certificate and provider credential fields are present in Render.
  Their presence alone is not acceptance evidence.
- The user replaced the old Render database connection using the private
  helper and saved it. Subsequent inspection of the saved Render field verified
  the selected Ireland Supabase project, `moore_runtime` pooler login, session
  pooler port 5432, `SSL Mode=VerifyFull`, bundled CA certificate path, and
  maximum pool size 20. The password was not displayed in tool output, and the
  field was hidden again afterward. This verifies saved configuration; the
  running API still requires a successful deployment and live health checks.
- No application deployment was triggered. The health-check path remains
  `/api/health` pending rollout of the release implementing `/health/ready`.

## Remaining dependencies

- Merge PR #7 to place the daily encrypted backup workflow on the default
  branch, then manually dispatch it and verify its GitHub artifact. GitHub only
  schedules workflows from the default branch. No scheduled-backup readiness
  declaration is set before that hosted run succeeds.
- Complete Brevo and Cloudinary credential-rotation/acceptance evidence and
  approved privacy/terms versions, URLs and retention configuration.
- After prerequisite acceptance, deploy the tested commit, switch the Render
  health path to `/health/ready`, verify public health, and rehearse restart
  recovery with the existing certificate.
- Finish initial administrators/MFA and guest/staff frontend acceptance.

An initial public readiness request timed out without an HTTP response; it
does not establish either a healthy or a failed current service.

## Production backup restore rehearsal completed

- Created an encrypted export of the selected production project's public
  schema, stored privately outside Git on this computer. This temporary local
  copy does not establish scheduled or durable off-provider backup coverage.
- Restored it into a disposable PostgreSQL 17 container on a Docker network
  with external connectivity disabled. No API workers were started.
- The first attempt exposed an actual runbook omission: the restored RLS
  policy references `moore_runtime`, which must exist before restoration.
  Created that role with NOLOGIN on the disposable target and corrected the
  deployment and recovery instructions.
- The repeated restore passed `verify-restore-drill.sh`; exact row manifests
  matched across all 48 public tables. The disposable container and network
  were removed. Production data was not changed.
- There are no production Data Protection keys yet, so recovery of an actual
  production encrypted payload still needs verification after deployment.

## Encrypted backup and alert setup completed

- Created `moore_backup` in the selected live Supabase project after explicit
  approval for its complete-data read scope. Live validation confirmed
  `BYPASSRLS` for complete logical exports and no write, DDL, ownership,
  replication, server-file, database-creation, role-creation or inherited-role
  privileges.
- A PostgreSQL 17 dump through the dedicated role completed. The repository's
  age-streaming backup command then created a 176,196-byte encrypted archive;
  decryption into PostgreSQL 17 `pg_restore --list` succeeded without writing a
  plaintext dump.
- Added the dedicated connection, age public recipient and Healthchecks ping URL
  as encrypted GitHub Actions repository secrets. Their values were not added
  to Git.
- Created the free Healthchecks.io project under
  `moorehotelsandsuites@gmail.com`, named the daily backup check, set a one-day
  period with four-hour grace, and enabled confirmed email integrations for
  `moorehotelsandsuites@gmail.com` and `lilswatch112@gmail.com`.
- Triggered a controlled failure and recovery. Healthchecks reported successful
  delivery to both email integrations and returned the check to Up. This proves
  backup alert routing; it does not establish the separate uptime, API latency,
  queue or payment alert categories.
