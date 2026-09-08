# Supabase production database

The API uses Supabase as managed PostgreSQL only. Browser clients must not use
Supabase Data API credentials for Moore Hotels application tables.

## Connection separation

- `MIGRATION_CONNECTION_STRING`: Supabase `postgres` owner through the IPv4
  session pooler on port 5432, with `SSL Mode=VerifyFull`. Use semicolon-separated
  Npgsql key/value syntax, never a URI, so scripts do not place credentials in
  process arguments. Render also supplies service variables to the runtime
  container, so its entrypoint removes the owner credential before executing
  .NET. It is not present in the API's initial process environment or config.
- `ConnectionStrings__DefaultConnection`: dedicated `moore_runtime` login
  through the same session pooler. Never use `postgres`, `service_role`, port
  6543 transaction mode, or the Supabase service key here. Start with
  `Maximum Pool Size=20`; the API rejects values above 32 per instance.
- `DATABASE_RUNTIME_ROLE=moore_runtime`.
- `Database__Provider=Supabase` and `Database__TrustRenderPrivateNetwork=false`.

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

1. Create a new Supabase project in the intended production region and record
   its session-pooler owner connection from **Connect**.
2. Generate a unique runtime password in a password manager.
3. Run `scripts/create-supabase-runtime-role.sh` with the three required
   environment variables. The same command safely rotates an existing runtime
   role password. Remove `DATABASE_RUNTIME_PASSWORD` afterward.
4. Configure Render's owner and runtime connection strings separately.
5. Deploy. The pre-deploy command applies EF migrations, validates relational
   invariants, binds the database to `production`, revokes all Data API access
   to `public`, and grants only the required runtime privileges.
6. Run `scripts/validate-runtime-database-role.sh` with the runtime connection.
7. Confirm `/health/ready`, create a synthetic booking, verify its email, then
   delete the synthetic record before opening production traffic.

Use a direct IPv6 connection for migrations and backup tools only when the
runner supports IPv6. Render deployments should use the IPv4 session pooler.
