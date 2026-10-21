// Integration test: needs a PostgreSQL superuser URL in TEST_ADMIN_DATABASE_URL
// (local: postgres://postgres@localhost:55432/postgres). Skips when it is not set.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { after, before, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import pg from 'pg';
import { loadMigrations, migrate, type Migration } from './migrate.js';

const adminUrl = process.env.TEST_ADMIN_DATABASE_URL;
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const testDb = `rwbr_migrate_test_${process.pid}`;

function urlFor(db: string, user: string): string {
  const u = new URL(adminUrl!);
  u.pathname = `/${db}`;
  u.username = user;
  return u.toString();
}

async function connect(url: string): Promise<pg.Client> {
  const c = new pg.Client({ connectionString: url });
  await c.connect();
  return c;
}

describe('migrations', { skip: !adminUrl && 'TEST_ADMIN_DATABASE_URL not set' }, () => {
  let admin: pg.Client;
  let migrations: Migration[];

  before(async () => {
    admin = await connect(adminUrl!);
    // Roles are cluster-wide; create them if this cluster has never been bootstrapped.
    for (const role of ['rwbr_migrator', 'rwbr_app']) {
      const { rowCount } = await admin.query('SELECT 1 FROM pg_roles WHERE rolname = $1', [role]);
      if (!rowCount) await admin.query(`CREATE ROLE ${role} LOGIN`);
    }
    await admin.query(`DROP DATABASE IF EXISTS ${testDb}`);
    await admin.query(`CREATE DATABASE ${testDb} OWNER rwbr_migrator`);
    const db = await connect(urlFor(testDb, 'postgres'));
    const bootstrap = await readFile(path.join(root, 'bootstrap/roles.sql'), 'utf8');
    // Reuse the schema/default-privilege part of the bootstrap (everything after \connect).
    await db.query(bootstrap.split(/^\\connect .*$/m)[1]!);
    await db.end();
    migrations = await loadMigrations(path.join(root, 'migrations'));
  });

  after(async () => {
    await admin.query(`DROP DATABASE IF EXISTS ${testDb} WITH (FORCE)`);
    await admin.end();
  });

  it('applies every migration on a clean database, then is a no-op', async () => {
    const c = await connect(urlFor(testDb, 'rwbr_migrator'));
    try {
      const first = await migrate(c, migrations);
      assert.deepEqual(
        first,
        migrations.map((m) => `${m.version}_${m.name}`),
      );
      assert.deepEqual(await migrate(c, migrations), []);
    } finally {
      await c.end();
    }
  });

  it('refuses to run when an applied migration was edited', async () => {
    const c = await connect(urlFor(testDb, 'rwbr_migrator'));
    try {
      const edited = migrations.map((m, i) => (i === 0 ? { ...m, checksum: 'tampered' } : m));
      await assert.rejects(migrate(c, edited), /was modified after it was applied/);
    } finally {
      await c.end();
    }
  });

  it('lets the app role create users but not delete them or run DDL', async () => {
    const app = await connect(urlFor(testDb, 'rwbr_app'));
    try {
      const { rows } = await app.query<{ id: string }>(
        'INSERT INTO users DEFAULT VALUES RETURNING id',
      );
      await app.query(
        `INSERT INTO auth_identities (user_id, provider, provider_subject) VALUES ($1, 'GUEST', 'dev-1')`,
        [rows[0]!.id],
      );
      await assert.rejects(app.query('DELETE FROM users'), /permission denied/);
      await assert.rejects(app.query('CREATE TABLE hack (id int)'), /permission denied/);
      await assert.rejects(
        app.query(`INSERT INTO schema_migrations VALUES ('9999', 'x', 'x')`),
        /permission denied/,
      );
    } finally {
      await app.end();
    }
  });

  it('rejects duplicate identities and invalid status values', async () => {
    const app = await connect(urlFor(testDb, 'rwbr_app'));
    try {
      const { rows } = await app.query<{ id: string }>(
        'INSERT INTO users DEFAULT VALUES RETURNING id',
      );
      await assert.rejects(
        app.query(
          `INSERT INTO auth_identities (user_id, provider, provider_subject) VALUES ($1, 'GUEST', 'dev-1')`,
          [rows[0]!.id],
        ),
        /duplicate key/,
      );
      await assert.rejects(
        app.query(`UPDATE users SET status = 'WHATEVER' WHERE id = $1`, [rows[0]!.id]),
        /check constraint/,
      );
    } finally {
      await app.end();
    }
  });
});
