# Database (PostgreSQL)

Versioned SQL migrations and the runner. Design rules: BRD v1.2 §9.

- `bootstrap/roles.sql` — run once per environment as a superuser. Creates `rwbr_migrator` (owns the schema, runs migrations) and `rwbr_app` (used by the API; cannot run DDL or delete rows unless a migration grants it).
- `migrations/NNNN_name.sql` — applied in order, one transaction each, recorded in `schema_migrations` with a SHA-256 checksum. **Never edit an applied migration**; the runner refuses to start if one changed. Fix forward with a new file.

## Local setup without Docker (Windows, PostgreSQL 18 installed)

A throwaway cluster on port 55432 with trust auth on localhost only, kept in `.local/` (gitignored):

```bash
PG="/c/Program Files/PostgreSQL/18/bin"
"$PG/initdb" -D .local/pgdata -U postgres --auth=trust -E UTF8 --locale=C
"$PG/pg_ctl" -D .local/pgdata -o "-p 55432 -c listen_addresses=localhost" -l .local/pg.log start
"$PG/psql" -h localhost -p 55432 -U postgres -d postgres -f database/bootstrap/roles.sql
MIGRATOR_DATABASE_URL=postgres://rwbr_migrator@localhost:55432/rwbr_dev pnpm db:migrate
```

With Docker, `infrastructure/docker/compose.yaml` does the same on port 55432.

## Tests

```bash
TEST_ADMIN_DATABASE_URL=postgres://postgres@localhost:55432/postgres pnpm --filter @rwbr/database test
```

Creates a temporary database, applies all migrations, checks re-runs are no-ops, edited migrations are rejected, and the `rwbr_app` role's permissions.
