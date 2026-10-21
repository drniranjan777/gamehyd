import path from 'node:path';
import { fileURLToPath } from 'node:url';
import pg from 'pg';
import { loadMigrations, migrate } from './migrate.js';

const url = process.env.MIGRATOR_DATABASE_URL;
if (!url) {
  console.error('MIGRATOR_DATABASE_URL is not set (connect as rwbr_migrator, not the app role).');
  process.exit(1);
}

const dir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../migrations');
const client = new pg.Client({ connectionString: url });
await client.connect();
try {
  const applied = await migrate(client, await loadMigrations(dir));
  console.log(applied.length ? `Applied: ${applied.join(', ')}` : 'Database is up to date.');
} catch (err) {
  console.error((err as Error).message);
  process.exitCode = 1;
} finally {
  await client.end();
}
