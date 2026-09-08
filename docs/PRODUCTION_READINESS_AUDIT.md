# Production readiness audit

Audit date: 7 September 2026

## Scope and conclusion

The API repository was reviewed across its Domain, Application,
Infrastructure, WebAPI, unit-test, integration-test, database migration,
deployment, CI and operational documentation layers (340 C# source files plus
the release configuration and scripts).

The API source is ready for review and a staging release: deterministic
restore, formatting, compilation, analyzers, tests, migrations, publish and the
production Linux container all pass. Changes remain in the local working tree;
no commit, push or deployment was performed.

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
12. Pinned .NET 10.0.11 runtime/framework packages, SDK 10.0.400, EF tooling and
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
20. Added managed-backup/PITR/off-provider-copy declarations, RPO/RTO,
    quarterly restore evidence, a guarded restore verifier and actionable queue,
    payment and recovery health signals.
21. Added evidence-backed Brevo and Cloudinary production acceptance gates;
    enabling Monnify additionally requires sandbox, webhook, live payment/refund,
    hosted-payment-page and PCI responsibility evidence.
22. Upgraded all projects, framework packages, EF tooling, CI and Docker to the
    supported .NET 10 LTS patch line and resolved its new obsolescence warnings.
23. Added audited rate-plan administration, room/category daily prices,
    promotions, taxes/fees, expiring token-protected quotes, immutable nightly
    breakdowns, atomic quote/promotion consumption and historical booking price
    snapshots.
24. Separated sellable room types from physical rooms, added per-night
    inventory and closure calculations, multi-room reservation units, deferred
    room assignment and concurrency-safe overbooking prevention.
25. Added an append-only folio with room/add-on charges, taxes, discounts,
    credits, deposits and split payments, reversal entries, partial refunds,
    post-payment incidentals, checkout settlement and reconciliation-safe
    revenue calculations.
26. Linked every PII-bearing email outbox entry to its guest data subject so
    export, merge, erasure and retention operations cannot leave hidden queued
    copies behind.
27. Added dormant linked-Client retention, account activity/status markers,
    credential and external-identity deletion, avatar cleanup, privacy-record
    minimization and persistence-boundary audit JSON redaction.
28. Hardened a fresh Supabase PostgreSQL deployment with separate owner/runtime
    connections, session-pooler and VerifyFull requirements, revoked Data API
    roles, least-privilege provisioning and startup privilege verification.
29. Removed account/reset/booking PII and secrets from API query strings and
    replaced SignalR JWT query transport with short-lived single-use tickets.
30. Added the separately authenticated, serialized emergency administrator
    suspension/reactivation workflow while preserving a final operational Admin.
31. Pinned CI actions and container/service images to immutable commits/digests,
    pinned Trivy 0.74.0, added repository-secret and high/critical container CVE
    gates, rehearsed migration rollback/reapply and Supabase privilege boundaries,
    and made encrypted persistent Data Protection readiness fail closed during
    Production startup.
32. Hardened the HTTP pipeline and startup configuration with bounded request,
    header and SignalR sizes, normalized CORS/host validation, global and
    endpoint rate limits, strict HS256 JWT validation, 12-character password
    minimums, sanitized production exceptions/logging, and fail-closed database
    connection and runtime-role checks.
33. Enforced Local/Production isolation at every shared boundary: conflicting
    process environment names fail closed; Local accepts only loopback or
    explicit test-container PostgreSQL with Local/test database names; Local
    cannot trust production proxies or call Brevo, Cloudinary or Monnify;
    JWT issuer/audience and Data Protection application names are distinct; and
    a database singleton stamp prevents either runtime or pre-deploy process
    from opening, migrating or rebinding the other environment's database.

## Verification evidence

- Locked NuGet restore: passed for all six projects.
- Release build with warnings as errors: 0 warnings, 0 errors.
- Recommended runtime analyzer rebuild: 0 warnings, 0 errors.
- Formatting verification: 0 files require changes.
- Unit tests: 19 passed, 0 failed, 0 skipped.
- PostgreSQL integration tests: 253 passed, 0 failed, 0 skipped.
- Live NuGet advisory scan, including transitive dependencies: passed with no
  reported vulnerable packages.
- EF model comparison: no model changes pending migration.
- Linux migration bundle: applied all 27 migrations to an empty PostgreSQL 16
  database; the latest migration also applied to an existing upgraded database,
  and a second bundle run required no migrations.
- Database invariant preflight: passed.
- Local Release publish: passed.
- Production Linux container build: passed as the non-root `app` user; no PDBs
  were shipped, the persistent key directory is mode `0700`, and only the seven
  required deployment scripts are present.
- Docker Scout high/critical release gate: passed. The runtime layer is upgraded
  from current Ubuntu repositories during the build. This remediated 21 of the
  28 initial findings. The final unfiltered scan reports 0 critical, 0 high,
  4 medium and 3 low findings; all seven residual Ubuntu advisories are marked
  by the scanner as having no vendor fix yet.
- Production-mode pre-deploy: passed over VerifyFull TLS with separate owner and
  least-privileged runtime roles, invariant validation and Data API hardening.
- Production-mode readiness: HTTP 200 with the database connected through the
  restricted role; restart retained the certificate-encrypted `0600` key ring.
- Production-mode liveness: HTTP 200.
- Security response headers: CSP, HSTS, frame denial, no-sniff, restrictive
  permissions policy, no-referrer and no-index were present.
- An untrusted browser origin received no CORS allow-origin header.
- Trivy 0.74.0 deployable-source secret scan, secret-pattern scan and diff
  whitespace checks: passed. The Git-ignored local environment file remains
  owner-only mode `0600` and is not part of the repository or Docker context.
- Local/Production negative isolation tests: Local startup rejected a
  Production-stamped database; Production binding rejected a Local-stamped
  database; the restricted runtime role was denied an attempted stamp update.
  The Local image then passed its full health, CORS, host, environment-header,
  authentication and authorization smoke suite against a Local-stamped
  disposable database.
- Trivy 0.74.0 production-image archive secret scan: passed with no embedded
  secret findings.
- No files were committed, pushed or deployed. Disposable test infrastructure
  was used locally only.

## Required release-owner steps

1. Review this working-tree diff and the migrations with a second engineer.
2. Complete `docs/FRONTEND_INTEGRATION_CHECKLIST.md` in the separate guest and
   dashboard repositories, especially Paystack removal, fragment-token parsing
   and staff MFA screens.
3. Rotate and set every production credential listed in
   `PRODUCTION_DEPLOYMENT.md`; never copy the disposable test values used by the
   automated suite.
4. Create the new Supabase project and follow
   `docs/SUPABASE_PRODUCTION_DATABASE.md`. Because the expired Render database
   is intentionally being replaced, initialize the empty Supabase database with
   the bundle; do not import stale data unless it is separately reviewed.
5. Enable managed backup/PITR, create the encrypted off-provider backup, perform
   a restore drill, and record genuine evidence before Production startup.
6. Deploy to staging and exercise real Brevo delivery and Cloudinary
   upload/delete behavior. Keep Monnify disabled until its sandbox webhook,
   server verification, low-value payment and refund acceptance all pass.
7. Enroll two separately controlled initial Admins, then every Manager and Staff
   account, in MFA and securely store one-time recovery codes.
8. Run the configured GitHub checks and CodeQL workflow. Only after every check
   and the frontend/provider acceptance steps pass should an authorized person
   merge or deploy.
