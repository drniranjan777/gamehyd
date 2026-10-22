/**
 * Creates an admin account from the command line; used once to bootstrap the first SUPER_ADMIN, after which admins
 * are created in the portal. The password comes from ADMIN_PASSWORD (never argv, which other users can see).
 *
 *   ADMIN_PASSWORD=... pnpm admin:create --email ops@example.com --name "Ops Lead" --role SUPER_ADMIN
 *
 * Prints the TOTP secret once; enrol it in an authenticator app before signing in.
 */
import 'reflect-metadata';
import { parseArgs } from 'node:util';
import { Pool } from 'pg';
import { AdminAuthService, ADMIN_ROLES } from './admin/admin-auth';
import { loadConfig } from './config';
import { Db } from './database/database.module';

async function main(): Promise<void> {
  const { values } = parseArgs({
    options: {
      email: { type: 'string' },
      name: { type: 'string' },
      role: { type: 'string', multiple: true },
    },
  });
  const password = process.env.ADMIN_PASSWORD;
  if (!values.email || !values.name || !values.role?.length || !password) {
    throw new Error(
      'usage: ADMIN_PASSWORD=... admin:create --email <email> --name <name> --role <ROLE> [--role <ROLE>]',
    );
  }
  const unknown = values.role.filter((r) => !(ADMIN_ROLES as readonly string[]).includes(r));
  if (unknown.length)
    throw new Error(`unknown role(s): ${unknown.join(', ')}; roles are ${ADMIN_ROLES.join(', ')}`);

  const config = loadConfig();
  const pool = new Pool({ connectionString: config.DATABASE_URL, max: 1 });
  try {
    const auth = new AdminAuthService(new Db(pool), config);
    const r = await auth.createAdmin(
      { email: values.email, displayName: values.name, password, roles: values.role },
      'system:cli',
    );
    console.log(`Created admin ${r.admin.email} (${r.admin.roles.join(', ')})`);
    console.log(`TOTP secret (shown once): ${r.totpSecret}`);
    console.log(`Authenticator URL: ${r.otpauthUrl}`);
  } finally {
    await pool.end();
  }
}

main().catch((err: Error) => {
  console.error(err.message);
  process.exit(1);
});
