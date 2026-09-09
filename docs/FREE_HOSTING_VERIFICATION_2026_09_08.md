# Supabase Free / Render Free verification — 2026-09-08

The owner chose to keep both services free. This supersedes the paid-plan,
Render disk and managed/PITR prerequisites in the earlier release review.

## Resulting deployment

- Supabase project: Moore hotel and suites, `ihxhqfffrxmgqabrncrg`.
- Render hosts only the API on Free compute. The Blueprint targets the existing
  `MooreHotelAndSuites-Api` name, has no database/disk/pre-deploy resource, and
  disables automatic deployment so migration and deployment occur serially.
- Removed the Render private-database TLS exception. Production database
  connections require full TLS verification; Local PostgreSQL remains isolated.
- Data Protection uses Microsoft's EF-backed key repository. A new EF migration
  adds `data_protection_keys`, with RLS and append-only runtime privileges.
  Keys remain encrypted with the separately backed-up PFX. Startup checks key
  readability and database writes without retaining its test row.
- `scripts/deploy-database.sh TESTED_IMAGE` runs migrations and role verification
  in an external disposable container. Owner credentials stay off Render.
- `BackupMode=Logical` permits truthful false managed-backup/PITR flags while
  retaining encrypted off-provider backup and restore-evidence requirements.
  The authenticated health response includes the backup mode. The free profile
  sets a daily backup RPO target instead of implying hourly PITR protection.
- `scripts/backup-production.sh` streams the public application schema into age
  encryption and refuses insecure TLS or overwriting an existing output.
  The operator must schedule exports outside the sleeping web service.

## Verification completed serially

- Release build and recommended analyzers: zero warnings/errors.
- Unit suite: 19 passed. Full PostgreSQL 17 integration suite: 259 passed,
  zero failed/skipped. Total: 278.
- New regressions cover database-backed key recovery after replacing the
  provider, encrypted XML at rest, wrong-certificate rejection without key
  replacement, logical-backup configuration and invalid storage modes.
- All 28 migrations passed up/down/up on isolated PostgreSQL 17. The model
  matches its migration. Runtime and Supabase Data API boundary scripts passed.
- Runtime key SELECT works; UPDATE, DELETE and TRUNCATE are denied. RLS grants
  key access only to the configured runtime role. Active API connections used
  TLS 1.3 with the configured VerifyFull certificate validation.
- Actual Production container started with no disk or key directory. A full
  container replacement retained the ability to decrypt an earlier payload.
- Encrypted logical export and restore passed. Exact row contents matched in
  all 48 public tables, including the encrypted keys and migration history.
  Overwrite and insecure-TLS backup attempts were rejected.
- The restore test exposed the pre-existing `public` schema in a fresh database;
  the isolated restore procedure now uses `--clean --if-exists` and explicitly
  limits that command to a fresh non-production target.
- Restored database keys decrypted the original protected payload, and the
  restored database started a new Production container with the original PFX.
- Production startup rejected a wrong valid certificate and missing encrypted
  backup readiness. It also rejected readable existing keys when RLS denied
  new key writes. Formatting, YAML, all shell syntax and diff checks passed.
- Trivy source secret scan: zero findings. Image HIGH/CRITICAL vulnerability
  scan: zero findings, including unfixed vulnerabilities.
- Verified image: `moore-hotels-api:free-20260908`,
  `sha256:b0e6c0f8132c80fca0d848aa8e6e94d4d5b74d27b7267886b188c57ae40bde84`.

## Live setup still required

These tests used synthetic credentials, certificates and isolated PostgreSQL,
not guest records. No production schema/data was changed, no hosting upgrade
was purchased, and no code was pushed or deployed during this change.

The selected live project still needs its runtime role and migrations, securely
configured connection/PFX credentials, an actual external export schedule and
restore evidence, and remaining provider/alert acceptance. Run those steps
before manually deploying the tested commit to the existing Render service.
No Render database resource was deleted; its application integration was removed.

Free-tier idle sleep, cold starts, quotas and delayed background work remain
operating constraints. This change does not promise continuous availability or
point-in-time recovery. See `PRODUCTION_DEPLOYMENT.md` for the current procedure.
