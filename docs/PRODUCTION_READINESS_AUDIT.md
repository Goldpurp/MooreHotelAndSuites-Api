# Production readiness audit

Audit date: 4 September 2026

## Scope and conclusion

The API repository was reviewed across its Domain, Application,
Infrastructure, WebAPI, unit-test, integration-test, database migration,
deployment, CI and operational documentation layers (238 C# source files plus
the release configuration and scripts).

The API source is ready for review and a staging release: deterministic
restore, formatting, compilation, analyzers, tests, migrations, publish and the
production Linux container all pass. Changes are committed locally only; no
push or deployment was performed.

The whole product cannot yet be called production-accepted because this
repository does not contain the guest website or staff dashboard, and live
Brevo, Cloudinary and optional Monnify transactions cannot be exercised without
the deployment owner's credentials and provider accounts. Those are release
acceptance steps, not hidden code failures; see the checklist below.

## Completed fixes

1. Archived the incomplete Paystack runtime. Its controller, processor,
   settings and dependency registrations were removed. New Paystack bookings
   are rejected in validation and the service layer; the enum remains only for
   historical database compatibility.
2. Secured guest booking lookup and invoice access with protected access
   tokens, non-enumerating responses and account ownership checks.
3. Added add-on catalog and booking add-on persistence, constraints, indexes,
   validation, atomic price updates and correct invoice itemization without
   double-counting totals.
4. Corrected analytics calculations and replaced placeholder operational KPIs
   with the authoritative analytics service.
5. Added hotel identity and `Africa/Lagos` time handling so check-in/check-out
   calendar dates are converted at the configured hotel hours rather than
   being incorrectly treated as UTC.
6. Made registration, staff creation/status changes, profile changes, password
   rotation and related audit/email writes atomic. Database conflicts return a
   controlled response instead of a generic server error.
7. Removed account-enumerating registration and login differences, moved
   sensitive frontend tokens into URL fragments, and added required staff MFA
   enrollment, TOTP login and recovery-code support.
8. Replaced direct transactional-email sends with an encrypted durable outbox,
   stable provider idempotency keys, bounded retry/lease handling, dead-letter
   health reporting and an admin retry API.
9. Removed unsafe process-local room caching, enforced room image limits under
   a row lock, tracked general media ownership and rejected deletion of unknown
   or actively referenced provider assets.
10. Added per-user notification receipts so one staff member cannot mark a
    broadcast notification read for everyone. Cancellation timestamps and
    stable operation-ledger IDs are now durable.
11. Added five reviewed migrations for secure guest access/email outbox,
    add-ons, general media, durable notification/cancellation state and the
    remaining production controls. Foreign keys, uniqueness rules, check
    constraints and query indexes are included.
12. Pinned .NET 8.0.30 runtime/framework packages, SDK 8.0.424, EF tooling and
    dependency lock files. CI now enforces locked restore, formatting,
    warning-as-error build, unit/integration tests, strict runtime analyzers,
    vulnerability scanning, EF model checks, migration bundle creation,
    publish, Docker build, CodeQL and Dependabot.
13. Fixed the production database preflight so the documented Npgsql
    connection string is safely translated for `psql`; it also verifies add-on
    totals and cross-table media ownership.
14. Added immediate SignalR staff-session revocation with database role/status
    and security-stamp revalidation at connection time.
15. Added a transactional media-deletion outbox, leased retries, health
    diagnostics and audited administrator recovery for exhausted deletions.
16. Replaced permanent guest booking links with 24-hour initial and two-hour
    rotating links, removed the historical code-plus-email authorization path,
    and revoke links on cancellation or automatic expiry.
17. Added an Admin-only account-to-guest reconciliation queue with row locks,
    identity-evidence rules, uniqueness enforcement, audit records and session
    invalidation.
18. Added unique refund references, exact amount/channel/evidence records,
    idempotent retries, row-locked completion and two-person approval above the
    configured high-value threshold.
19. Automated production data preflight, migrations and least-privilege grants
    with separate credentials. Production startup rejects a mismatched or
    privileged runtime role.

## Verification evidence

- Locked NuGet restore: passed for all six projects.
- Release build with warnings as errors: 0 warnings, 0 errors.
- Recommended runtime analyzer rebuild: 0 warnings, 0 errors.
- Formatting verification: 0 files require changes.
- Unit tests: 14 passed, 0 failed, 0 skipped.
- PostgreSQL integration tests: 126 passed, 0 failed, 0 skipped.
- Live NuGet advisory scan, including transitive dependencies: passed with no
  reported vulnerable packages.
- EF model comparison: no model changes pending migration.
- Linux migration bundle: applied all 17 migrations to an empty PostgreSQL 16
  database and then completed a second run with no migrations required.
- Database invariant preflight: passed.
- Local Release publish: passed.
- Production Linux container build: passed as the non-root `app` user.
- Production-mode readiness: HTTP 200, database connected, email queue
  operational with zero exhausted messages.
- Production-mode liveness: HTTP 200.
- Security response headers: CSP, HSTS, frame denial, no-sniff, restrictive
  permissions policy, no-referrer and no-index were present.
- An untrusted browser origin received no CORS allow-origin header.
- Secret-pattern and diff whitespace checks: passed.
- No files were pushed or deployed; all disposable verification containers,
  databases, roles, images, bundles and certificate files were removed.

## Required release-owner steps

1. Review this working-tree diff and the migrations with a second engineer.
2. Complete `docs/FRONTEND_INTEGRATION_CHECKLIST.md` in the separate guest and
   dashboard repositories, especially Paystack removal, fragment-token parsing
   and staff MFA screens.
3. Rotate and set every production credential listed in
   `PRODUCTION_DEPLOYMENT.md`; never copy the disposable test values used by the
   automated suite.
4. Back up the live database, restore it to a rehearsal database, run
   `scripts/validate-production-database.sh`, then apply the bundled migrations
   to the rehearsal copy.
5. Deploy to staging and exercise real Brevo delivery and Cloudinary
   upload/delete behavior. Keep Monnify disabled until its sandbox webhook,
   server verification, low-value payment and refund acceptance all pass.
6. Enroll every Admin, Manager and Staff account in MFA and securely store the
   one-time recovery codes.
7. Run the configured GitHub checks and CodeQL workflow. Only after every check
   and the frontend/provider acceptance steps pass should an authorized person
   merge or deploy.
8. Upgrade and regression-test the repository on .NET 10 before .NET 8 support
   ends on 10 November 2026.
