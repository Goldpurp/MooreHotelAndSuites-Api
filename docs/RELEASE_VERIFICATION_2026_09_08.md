# Serial release verification — 8 September 2026

This record covers release items 1–4 requested by the deployment owner. A local
rehearsal is not evidence that production provider settings or backups exist.
No push or deployment is part of this verification.

## 1. Release contents

- Reviewed the release inventory: 399 source/configuration/documentation files
  before this record, including all 27 discoverable EF migrations. The historical
  `ShortBookingCodes` migration declares its discovery attributes inline.
- Required new migrations, DTOs, services, tests and deployment scripts are
  included together. Local environment secrets, runtime state and private-key
  artifacts are excluded from the release inventory.
- Trivy 0.74.0 scanned a copy of the complete Git release candidate (tracked and
  non-ignored untracked files): no secret findings. The scanner download was
  checked against its published SHA256 checksum.
- Every shell script passed syntax validation; `git diff --check` passed.
- Fixed CI's shell check: `bash -n scripts/*.sh` checked only the first file.
  The new loop checks each file. A controlled invalid second script confirmed
  the old command passed incorrectly and the replacement failed as required.
- Existing source changes were preserved in local commit `abbfdd9`; the working
  tree was verified clean after commit. Nothing was pushed.
  compilation and behavioral verification belong to item 2 below.

## 2. Build and tests

- Installed SDK 10.0.400 under a temporary tools directory and verified its
  SHA512 against Microsoft's release metadata. Started the installed Docker
  Desktop and an isolated PostgreSQL 16 container on loopback port 55439.
- Locked restore: all six projects passed; lockfiles unchanged.
- Release build: zero warnings/errors. The sandboxed build stalled; rerunning
  with normal local process access, `-m:1`, `UseSharedCompilation=false` and
  `--disable-build-servers` passed in 23 seconds.
- Unit tests: 19 passed, zero failed/skipped.
- PostgreSQL integration tests: 253 passed, zero failed/skipped (57 seconds).
- Formatting verification: passed without changes.
- Recommended .NET analyzer rebuild with warnings as errors: passed.
- Live NuGet vulnerability scan including transitive dependencies: passed.
- Pinned EF tooling restored; no model changes missing migrations.
- Production Docker image and bundled migrations built successfully. Image:
  `moore-hotels-api:release-20260908`, manifest
  `sha256:f203caece4f6396efd625a751422d6b73502541523689ff1045db3f43a8f2f83`.
- Container artifact checks: non-root user, executable migration bundle, API
  assembly present, no PDBs, key directory owned by `app` with mode `0700`.
- Trivy 0.74.0 image scan: zero HIGH/CRITICAL findings, including unfixed issues.
  Advisory database updated 2026-09-08T07:08:01Z. Trivy's registry downloader
  stalled, so the same official GHCR layer was downloaded with curl and SHA256
  verified before scanning with the current local database.
- These are local release gates. GitHub Actions and CodeQL on the eventual
  pushed commit remain remote release requirements.

## 3. Database deployment and recovery

- Confirmed and fixed an upgrade blocker: preflight queried
  `booking_quotes.AmendmentBookingId` before the migration introducing it.
  The previous-release schema reproduced PostgreSQL's missing-column error.
  Preflight now checks column existence before that invariant, and CI checks
  both the previous release and current schema.
- Confirmed and fixed a restore-verification gap: the old verifier accepted a
  database missing `environment_boundaries`. It now checks all application and
  Identity tables, the production marker, expected latest migration, and the
  full deployment invariants. CI exercises a real dump/restore, checks a data
  sentinel, and rejects a missing table and wrong migration version.
- Linux migration bundle: fresh migration, rollback to zero and reapply passed.
- Populated previous-release upgrade through the actual pre-deploy script:
  passed. All 27 migrations applied; one paid booking worth NGN 100,000 and its
  two folio entries survived; production binding and runtime grants passed.
- Supabase-like role hierarchy, including `service_role` BYPASSRLS and inherited
  Data API roles: hardening passed. Runtime schema creation, migration-history
  access, booking deletion, audit mutation and environment mutation were denied.
  All four Data API roles were denied application access. Runtime operational
  reads succeeded.
- Backup/restore used PostgreSQL 16 clients in a disposable Linux container.
  Exact row hashes matched across all 47 public tables and all sequence values.
  Recovered data included a booking, room, guest, reservation unit, two folio
  entries, audit entries and queued email. The new CI restore step was executed
  verbatim locally and passed, including its negative cases.
- YAML, every workflow shell block, every shell script and diff whitespace
  validation passed.
- Live limitation: the connected Supabase account lists two Moore Hotels
  projects, both INACTIVE. Neither has been selected as the target. No live
  schema, permissions, backup/PITR configuration or restore evidence has been
  approved by these local tests. No remote database was changed.

## 4. Production configuration and encryption-key persistence

- Confirmed and fixed a process-environment leak: clearing deployment secrets
  inside .NET left their original values in Linux `/proc/1/environ`. The image
  now removes both migration-owner and runtime-provisioning credentials before
  executing .NET. A new CI container gate proved the old image fails and the
  corrected image becomes healthy as non-root PID 1 without either credential.
- Confirmed and fixed read-only key storage being accepted when an existing key
  still worked. Startup now creates, writes, flushes and removes a storage probe.
- Confirmed and fixed a different valid PFX being accepted while existing keys
  became unreadable and a replacement key was generated. Startup now validates
  every retained non-revoked key before the protect/unprotect round-trip.
- Added three startup tests: invalid storage despite a working protector,
  probe cleanup, and wrong valid certificate rejection without replacement keys.
- Final Release build and recommended analyzers: zero warnings/errors.
  Formatting verification passed. Full integration rerun: 256 passed, zero
  failed/skipped. The 19 unit tests from item 2 also passed; their code and
  dependencies did not change. Total verified automated tests: 275.
- Final Linux image:
  `sha256:46a88e534953db160d0a0b29e2381e73f26ea0987a2afdaead55f6ff9a3ccf4d`.
  The CI container runtime gate passed again on this image. Its final Trivy
  scan reported zero HIGH/CRITICAL vulnerabilities using the current verified DB.
- Final source inventory: 402 files; Trivy secret scan passed. Lockfiles remained
  unchanged; final workflow YAML/shell syntax and diff whitespace checks passed.
- Production-mode startup returned HTTP 200 readiness with the dedicated
  runtime role and VerifyFull TLS. Server inspection confirmed TLS 1.3 for all
  active rehearsal runtime connections. Security headers were present.
- Restart retained the exact encrypted key-file hashes, `0700` key directory
  and `0600` XML files. A payload protected before the restart decrypted after
  replacement and restart using the API's application name and outbox purpose.
- Restoring the encrypted key-ring archive into a fresh volume, with the
  backed-up PFX/password, recovered that same payload and started Production.
- Seven actual container startup rejection cases passed: read-only existing key
  ring, missing PFX, wrong PFX password, valid but wrong PFX, untrusted database
  certificate, privileged runtime login, and missing backup-readiness declaration.
- All runtime/provider settings and certificates used here were synthetic
  fixtures on an isolated Docker network without internet egress. They are not
  production secrets or evidence of live provider/backup acceptance. Actual
  Render configuration, persistent-disk permissions and external recovery remain
  unverified until the owner identifies an active production target.

## Remaining external release gates

1. Select and activate/provision the intended production database; both visible
   Moore Hotels Supabase projects were INACTIVE during this review.
2. Configure real owner/runtime credentials, encryption certificate and disk,
   and genuine backup/PITR/restore/provider/alert evidence in the secret manager.
3. Run preflight and a protected restore rehearsal for that target, then verify
   the actual deployed configuration and restart behavior.
4. Push only when authorized and require GitHub Actions/CodeQL on the pushed
   commit. Guest/staff frontend and live provider acceptance remain outside
   these four release items.

## Live target follow-up, 2026-09-08

This follow-up supersedes the earlier target-selection limitation above.

- User selected **Moore hotel and suites**, project `ihxhqfffrxmgqabrncrg`,
  Goldpurp organization, Frankfurt (`eu-central-1`). Resumed the paused project
  and waited for `ACTIVE_HEALTHY` before final inspection.
- PostgreSQL reports 17.6. Live catalog inspection confirmed zero public tables,
  no other non-platform tables, no EF migration history, no environment marker,
  and no `moore_runtime` role. Executed the SQL body of the repository's
  production preflight through the Supabase connector: passed. This validates
  an empty starting schema, not migration deployment or runtime connectivity.
- The organization reports plan `free`. Managed backup/PITR configuration and
  off-provider recovery are still unverified; no readiness declarations changed.
- Inspected the existing Render API service `srv-d5uhns4oud1c73bn28o0` in its
  dashboard. It uses Free compute, branch `main`, and health path `/api/health`.
  Pre-deploy commands are unavailable on that plan; the Disk page explicitly
  says persistent disks require a paid compute plan. The minimum paid option
  displayed is $7/month (0.5 CPU, 512 MB), before disk and other usage charges.
- Required deployment configuration: paid compute, a persistent `/var/data`
  disk, pre-deploy `./scripts/predeploy-production.sh`, and `/health/ready`.
  Coordinate those changes with the tested release, since the old deployed
  version may not implement its readiness path. No Render settings were changed.
- Still needed: securely configured owner/runtime credentials, production PFX
  and key storage, paid backup/PITR setup, then live migration/runtime checks
  and a protected restore/restart rehearsal. No schema/data changes, paid
  upgrades, code push, or application deployment occurred in this follow-up.
