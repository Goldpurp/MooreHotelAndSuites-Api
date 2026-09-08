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

Pending.

## 4. Production configuration and encryption-key persistence

Pending.
