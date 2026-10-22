// Integration tests for the Phase 3 schema rules. Skips without TEST_ADMIN_DATABASE_URL.
import assert from 'node:assert/strict';
import { after, before, describe, it } from 'node:test';
import pg from 'pg';
import { adminUrl, connect, createTestDatabase, type TestDatabase } from './testdb.js';

describe('schema rules', { skip: !adminUrl && 'TEST_ADMIN_DATABASE_URL not set' }, () => {
  let db: TestDatabase;
  let app: pg.Client;
  let migrator: pg.Client;
  let userId: string;

  before(async () => {
    db = await createTestDatabase(`rwbr_schema_test_${process.pid}`);
    app = await connect(db.appUrl);
    migrator = await connect(db.migratorUrl);
    userId = (await app.query<{ id: string }>('INSERT INTO users DEFAULT VALUES RETURNING id'))
      .rows[0]!.id;
  });

  after(async () => {
    await app?.end();
    await migrator?.end();
    await db?.drop();
  });

  it('keeps the wallet ledger append-only, even for the schema owner', async () => {
    const w = await app.query<{ id: string }>(
      `INSERT INTO wallets (user_id, currency_code, balance, version) VALUES ($1, 'COIN', 50, 1) RETURNING id`,
      [userId],
    );
    await app.query(
      `INSERT INTO wallet_transactions (wallet_id, amount, balance_after, wallet_version, reason, reference, idempotency_key)
       VALUES ($1, 50, 50, 1, 'MATCH_REWARD', 'match:test', 'match:test:reward')`,
      [w.rows[0]!.id],
    );
    await assert.rejects(
      app.query('UPDATE wallet_transactions SET amount = 5000'),
      /permission denied/,
    );
    await assert.rejects(app.query('DELETE FROM wallet_transactions'), /permission denied/);
    await assert.rejects(
      migrator.query('UPDATE wallet_transactions SET amount = 5000'),
      /append-only/,
    );
    await assert.rejects(migrator.query('TRUNCATE wallet_transactions CASCADE'), /append-only/);

    // Same idempotency key twice on one wallet is impossible: retried grants cannot double-pay.
    await assert.rejects(
      app.query(
        `INSERT INTO wallet_transactions (wallet_id, amount, balance_after, wallet_version, reason, reference, idempotency_key)
         VALUES ($1, 50, 100, 2, 'MATCH_REWARD', 'match:test', 'match:test:reward')`,
        [w.rows[0]!.id],
      ),
      /duplicate key/,
    );
    const drift = await app.query('SELECT * FROM wallet_ledger_drift');
    assert.equal(drift.rowCount, 0);
    await assert.rejects(
      app.query(`UPDATE wallets SET balance = -1 WHERE id = $1`, [w.rows[0]!.id]),
      /check constraint/,
    );
  });

  it('freezes published map versions and enforces the match lifecycle', async () => {
    await assert.rejects(
      migrator.query(
        `UPDATE map_versions SET manifest = '{"halfSize": 5}' WHERE version = 'greybox-block-100-v2'`,
      ),
      /immutable/,
    );
    const server = await app.query<{ id: string }>(
      `INSERT INTO game_servers (address, region, build_version) VALUES ('10.0.0.1:7777', 'IN-SOUTH', 'test') RETURNING id`,
    );
    const match = await app.query<{ id: string }>(
      `INSERT INTO matches (game_server_id, map_version, mode, region, capacity, ruleset_version)
       VALUES ($1, 'greybox-block-100-v2', 'SOLO', 'IN-SOUTH', 30, 'v1') RETURNING id`,
      [server.rows[0]!.id],
    );
    const id = match.rows[0]!.id;
    await app.query(`UPDATE matches SET status = 'ACTIVE' WHERE id = $1`, [id]);
    await assert.rejects(
      app.query(`UPDATE matches SET status = 'FORMING' WHERE id = $1`, [id]),
      /not allowed/,
    );
    await assert.rejects(
      app.query(`UPDATE matches SET status = 'COMPLETED' WHERE id = $1`, [id]),
      /check constraint/, // a completed match must carry its result hash
    );
    await app.query(
      `UPDATE matches SET status = 'COMPLETED', result_sha256 = decode(repeat('ab', 32), 'hex') WHERE id = $1`,
      [id],
    );
    await assert.rejects(
      app.query(`UPDATE matches SET duration_seconds = 1 WHERE id = $1`, [id]),
      /immutable/,
    );
    await assert.rejects(
      app.query(`UPDATE match_verification_records SET outcome = 'ACCEPTED'`),
      /permission denied/,
    );
  });

  it('only lets players equip cosmetics they own, in the matching slot', async () => {
    const skins = await app.query<{ id: string; key: string }>(
      `SELECT id, key FROM cosmetics ORDER BY key`,
    );
    const skin = skins.rows.find((r) => r.key === 'skin_rifle_default')!;
    const outfit = skins.rows.find((r) => r.key === 'outfit_default')!;
    await assert.rejects(
      app.query(
        `INSERT INTO loadout_slots (user_id, slot, cosmetic_id) VALUES ($1, 'WEAPON_SKIN', $2)`,
        [userId, skin.id],
      ),
      /foreign key/,
    );
    await app.query(
      `INSERT INTO player_inventory (user_id, cosmetic_id, source, source_reference) VALUES ($1, $2, 'STARTER', 'signup'), ($1, $3, 'STARTER', 'signup')`,
      [userId, skin.id, outfit.id],
    );
    await app.query(
      `INSERT INTO loadout_slots (user_id, slot, cosmetic_id) VALUES ($1, 'WEAPON_SKIN', $2)`,
      [userId, skin.id],
    );
    await assert.rejects(
      app.query(`INSERT INTO loadout_slots (user_id, slot, cosmetic_id) VALUES ($1, 'EMOTE', $2)`, [
        userId,
        outfit.id,
      ]),
      /does not fit slot/,
    );
  });

  it('rejects cosmetics that carry competitive stats (no pay-to-win)', async () => {
    await assert.rejects(
      migrator.query(
        `INSERT INTO cosmetics (key, kind, display_name, rarity, attributes) VALUES ('skin_op', 'WEAPON_SKIN', 'OP', 'LEGENDARY', '{"damage": 5}')`,
      ),
      /check constraint/,
    );
  });

  it('keeps the audit log append-only and reports sane', async () => {
    await app.query(
      `INSERT INTO admin_audit_logs (actor, action, entity_type, entity_id) VALUES ('system', 'test', 'user', $1)`,
      [userId],
    );
    await assert.rejects(app.query('DELETE FROM admin_audit_logs'), /permission denied/);
    await assert.rejects(migrator.query('DELETE FROM admin_audit_logs'), /append-only/);
    await assert.rejects(
      app.query(
        `INSERT INTO player_reports (reporter_user_id, reported_user_id, reason) VALUES ($1, $1, 'CHEATING')`,
        [userId],
      ),
      /check constraint/,
    );
  });

  it('protects guest credentials and refresh tokens', async () => {
    await assert.rejects(
      app.query(
        `INSERT INTO auth_identities (user_id, provider, provider_subject) VALUES ($1, 'GUEST', 'device-x')`,
        [userId],
      ),
      /check constraint/, // a guest identity needs a credential hash
    );
    await assert.rejects(
      app.query(
        `INSERT INTO player_sessions (user_id, family_id, refresh_token_hash, expires_at) VALUES ($1, gen_random_uuid(), '\\x00', now() + interval '1 day')`,
        [userId],
      ),
      /check constraint/, // hashes only, 32 bytes
    );
    await assert.rejects(app.query('DELETE FROM player_sessions'), /permission denied/);
  });
});
