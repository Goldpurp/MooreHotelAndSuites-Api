# Supabase production database

The API uses Supabase as managed PostgreSQL only. Browser clients must not use
Supabase Data API credentials for Moore Hotels application tables.

## Selected production target

- Project: **Moore hotel and suites** (`ihxhqfffrxmgqabrncrg`).
- Organization: Goldpurp (`tpajdexfeepffldbsopr`); region: `eu-central-1`.
- Resumed and confirmed `ACTIVE_HEALTHY` on 2026-09-08, PostgreSQL 17.6.
- Live inspection found zero public application tables, no EF migration
  history, no environment marker, and no `moore_runtime` role. The release's
  SQL preflight passed. This is an uninitialized application database.
- The organization is on Free. The chosen free plan uses encrypted logical exports, not managed backups or
  PITR. An off-provider restore rehearsal remains outstanding.

## Connection separation

- `MIGRATION_CONNECTION_STRING`: Supabase `postgres` owner through the IPv4
  session pooler on port 5432, with `SSL Mode=VerifyFull`. Use semicolon-separated
  Npgsql key/value syntax, never a URI, so scripts do not place credentials in
  process arguments. Supply this only to the external migration runner, never
  to Render. The API entrypoint also strips it as defense in depth.
- `ConnectionStrings__DefaultConnection`: dedicated `moore_runtime` login
  through the same session pooler. Never use `postgres`, `service_role`, port
  6543 transaction mode, or the Supabase service key here. Start with
  `Maximum Pool Size=20`; the API rejects values above 32 per instance.
- `DATABASE_RUNTIME_ROLE=moore_runtime`.
- `Database__Provider=Supabase`. No Render database or private-network exception.

The pre-deploy connection is also environment-bound. Before migration it
rejects Local/test/restore targets and any database already stamped for another
environment. Immediately after the first migration it inserts the singleton
`production` marker. The API's runtime role can read that marker for startup
verification but cannot modify it. Local startup uses a distinct `local` marker
and refuses a Production-bound database.

For a pooler username, append the project reference, for example
`moore_runtime.<project-ref>`. Keep `DATABASE_RUNTIME_ROLE` as the underlying
PostgreSQL role name without the project suffix.

The total maximum across every API replica must stay within the project and
pooler connection budget. Recalculate the per-instance limit before scaling
beyond one Render instance.

## One-time project bootstrap

1. Use the selected project above and record its session-pooler owner connection
   from **Connect**. Recheck its schema and migration history before bootstrap;
   do not create a replacement project or overwrite unexpected existing data.
2. Generate a unique runtime password in a password manager.
3. Run `scripts/create-supabase-runtime-role.sh` with the three required
   environment variables. The same command safely rotates an existing runtime
   role password. Remove `DATABASE_RUNTIME_PASSWORD` afterward.
4. Configure only the runtime connection on Render; keep the owner secret on
   the external runner.
5. Run `scripts/deploy-database.sh TESTED_IMAGE` externally before manually
   deploying that same release to Render Free. The command applies EF migrations, validates relational
   invariants, binds the database to `production`, revokes all Data API access
   to `public`, and grants only the required runtime privileges.
6. Run `scripts/validate-runtime-database-role.sh` with the runtime connection.
7. Confirm `/health/ready`, create a synthetic booking, verify its email, then
   delete the synthetic record before opening production traffic.

Use a direct IPv6 connection for migrations and backup tools only when the
runner supports IPv6. Render deployments should use the IPv4 session pooler.
