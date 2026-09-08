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
- Existing source changes are being preserved as one local release candidate;
  compilation and behavioral verification belong to item 2 below.

## 2. Build and tests

Pending.

## 3. Database deployment and recovery

Pending.

## 4. Production configuration and encryption-key persistence

Pending.
