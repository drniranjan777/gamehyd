// CLI for other packages' tests: `node --import tsx src/testdb-cli.ts create <name>` prints JSON
// {"appUrl", "migratorUrl"}; `drop <name>` removes it. Needs TEST_ADMIN_DATABASE_URL.
import { createTestDatabase, adminUrl } from './testdb.js';

const [command, name] = process.argv.slice(2);
if (!adminUrl || !name || !/^[a-z0-9_]{3,60}$/.test(name)) {
  console.error('usage: testdb-cli create|drop <name>  (TEST_ADMIN_DATABASE_URL required)');
  process.exit(2);
}
const db = await createTestDatabase(name, command === 'create');
if (command === 'drop') await db.drop();
else console.log(JSON.stringify({ appUrl: db.appUrl, migratorUrl: db.migratorUrl }));
