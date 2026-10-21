import { createHash } from 'node:crypto';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import type { Client } from 'pg';

export interface Migration {
  version: string;
  name: string;
  sql: string;
  checksum: string;
}

const FILE_PATTERN = /^(\d{4})_([a-z0-9_]+)\.sql$/;
// Arbitrary constant; serialises concurrent migrators against the same database.
const LOCK_KEY = 7_420_001;

export async function loadMigrations(dir: string): Promise<Migration[]> {
  const files = (await readdir(dir)).filter((f) => f.endsWith('.sql')).sort();
  const migrations: Migration[] = [];
  for (const file of files) {
    const match = FILE_PATTERN.exec(file);
    if (!match) throw new Error(`Migration file name must look like 0001_name.sql: ${file}`);
    const sql = await readFile(path.join(dir, file), 'utf8');
    migrations.push({
      version: match[1]!,
      name: match[2]!,
      sql,
      checksum: createHash('sha256').update(sql).digest('hex'),
    });
  }
  const versions = new Set(migrations.map((m) => m.version));
  if (versions.size !== migrations.length) throw new Error('Duplicate migration version');
  return migrations;
}

/**
 * Applies pending migrations in order, each in its own transaction.
 * Refuses to run if an already-applied file was edited (checksum mismatch):
 * fix forward with a new migration instead.
 */
export async function migrate(client: Client, migrations: Migration[]): Promise<string[]> {
  await client.query('SELECT pg_advisory_lock($1)', [LOCK_KEY]);
  try {
    await client.query(`
      CREATE TABLE IF NOT EXISTS schema_migrations (
        version text PRIMARY KEY,
        name text NOT NULL,
        checksum text NOT NULL,
        applied_at timestamptz NOT NULL DEFAULT now()
      )`);
    const { rows } = await client.query<{ version: string; checksum: string }>(
      'SELECT version, checksum FROM schema_migrations',
    );
    const applied = new Map(rows.map((r) => [r.version, r.checksum]));

    for (const [version, checksum] of applied) {
      const file = migrations.find((m) => m.version === version);
      if (!file) throw new Error(`Database has migration ${version} that is missing on disk`);
      if (file.checksum !== checksum) {
        throw new Error(`Migration ${version}_${file.name} was modified after it was applied`);
      }
    }

    const newlyApplied: string[] = [];
    for (const m of migrations) {
      if (applied.has(m.version)) continue;
      await client.query('BEGIN');
      try {
        await client.query(m.sql);
        await client.query(
          'INSERT INTO schema_migrations (version, name, checksum) VALUES ($1, $2, $3)',
          [m.version, m.name, m.checksum],
        );
        await client.query('COMMIT');
      } catch (err) {
        await client.query('ROLLBACK');
        throw new Error(`Migration ${m.version}_${m.name} failed: ${(err as Error).message}`);
      }
      newlyApplied.push(`${m.version}_${m.name}`);
    }
    return newlyApplied;
  } finally {
    await client.query('SELECT pg_advisory_unlock($1)', [LOCK_KEY]);
  }
}
