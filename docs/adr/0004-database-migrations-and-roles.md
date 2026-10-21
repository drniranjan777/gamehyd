# ADR 0004 — Database migrations and roles

- **Status:** Accepted (22 Oct 2026)
- **Context:** BRD §9 requires versioned migrations, least-privilege roles and PostgreSQL 16–18.

## Decision

- **Plain SQL migrations** in `database/migrations/NNNN_name.sql`, applied by `database/src/migrate.ts`:
  - one transaction per file, under a PostgreSQL advisory lock (safe with concurrent deploys);
  - recorded in `schema_migrations` with a SHA-256 checksum; the runner **refuses to start** if an applied file changed (fix forward with a new migration).
- **Roles** (created by `database/bootstrap/roles.sql`, run once per environment by a superuser):
  - `rwbr_migrator` owns the schema and runs migrations;
  - `rwbr_app` is used by the API: `SELECT` by default, write grants per table in migrations, no DDL, no `DELETE` unless granted.
- No ORM-generated migrations. The API may use a query builder later, but the SQL files are the source of truth.
- Target PostgreSQL 18 (dev machine has 18.4); production on a managed service with point-in-time recovery.

## Evidence

`database/src/migrate.test.ts` (4 tests against a temporary database): clean apply then no-op re-run, edited migration rejected, `rwbr_app` can insert users but cannot delete them, create tables or write `schema_migrations`, and constraint checks on identities and status.

## Consequences

- Down-migrations are not automated. Rollback is a reviewed forward migration plus point-in-time recovery for disasters (BRD §13).
- Migration `0001_users` creates `users` and `auth_identities`; the remaining tables of the 47-table design arrive in Phase 3.
