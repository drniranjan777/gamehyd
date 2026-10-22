// Test helper: a throwaway database with the bootstrap privileges and all migrations applied.
// Needs a superuser URL in TEST_ADMIN_DATABASE_URL (local: postgres://postgres@localhost:55432/postgres).
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import pg from 'pg';
import { loadMigrations, migrate } from './migrate.js';

export const adminUrl = process.env.TEST_ADMIN_DATABASE_URL;
export const databaseRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export function urlFor(db: string, user: string): string {
  const u = new URL(adminUrl!);
  u.pathname = `/${db}`;
  u.username = user;
  return u.toString();
}

export async function connect(url: string): Promise<pg.Client> {
  const c = new pg.Client({ connectionString: url });
  await c.connect();
  return c;
}

export interface TestDatabase {
  name: string;
  appUrl: string;
  migratorUrl: string;
  drop(): Promise<void>;
}

/** Creates `name`, runs the bootstrap grants and (optionally) every migration as rwbr_migrator. */
export async function createTestDatabase(
  name: string,
  applyMigrations = true,
): Promise<TestDatabase> {
  const admin = await connect(adminUrl!);
  for (const role of ['rwbr_migrator', 'rwbr_app']) {
    const { rowCount } = await admin.query('SELECT 1 FROM pg_roles WHERE rolname = $1', [role]);
    if (!rowCount) await admin.query(`CREATE ROLE ${role} LOGIN`);
  }
  await admin.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`);
  await admin.query(`CREATE DATABASE ${name} OWNER rwbr_migrator`);
  await admin.end();

  const db = await connect(urlFor(name, 'postgres'));
  const bootstrap = await readFile(path.join(databaseRoot, 'bootstrap/roles.sql'), 'utf8');
  await db.query(bootstrap.split(/^\\connect .*$/m)[1]!);
  await db.end();

  if (applyMigrations) {
    const m = await connect(urlFor(name, 'rwbr_migrator'));
    await migrate(m, await loadMigrations(path.join(databaseRoot, 'migrations')));
    await m.end();
  }

  return {
    name,
    appUrl: urlFor(name, 'rwbr_app'),
    migratorUrl: urlFor(name, 'rwbr_migrator'),
    async drop() {
      const a = await connect(adminUrl!);
      await a.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`);
      await a.end();
    },
  };
}
