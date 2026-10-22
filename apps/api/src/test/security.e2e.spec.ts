import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { parse as parseYaml } from 'yaml';
import { AdminAuthService } from '../admin/admin-auth';
import { base32Decode, currentStep, totpAt } from '../admin/admin-crypto';
import { Db } from '../database/database.module';
import { bearer, describeDb, guest, Harness, startApi } from './harness';

const spec = parseYaml(
  readFileSync(
    path.resolve(__dirname, '..', '..', '..', '..', 'packages', 'api-contracts', 'openapi.yaml'),
    'utf8',
  ),
) as { paths: Record<string, Record<string, { security?: Record<string, unknown>[] }>> };

const PARAMS: Record<string, string> = { key: 'some_product', mapKey: 'hyd-city', slot: 'OUTFIT' };
const concrete = (template: string) =>
  `/api/v1${template.replace(/\{(\w+)\}/g, (_, name: string) => PARAMS[name] ?? randomUUID())}`;

/** Every operation in the contract, with the credential it declares. */
const operations = Object.entries(spec.paths).flatMap(([p, ops]) =>
  Object.entries(ops).map(([method, op]) => ({
    method,
    path: p,
    scheme: op.security?.[0] ? Object.keys(op.security[0])[0] : 'none',
  })),
);

describeDb(
  'security: authorization matrix, sanctions on live tokens, token tampering (PostgreSQL)',
  () => {
    let h: Harness;
    let player: Awaited<ReturnType<typeof guest>>;
    let adminToken: string;

    beforeAll(async () => {
      h = await startApi(`rwbr_api_security_${process.pid}`, {
        RATE_LIMIT_ENABLED: 'false',
        UNHOSTED_MATCH_TIMEOUT_SECONDS: '10',
      });
      player = await guest(h);
      const created = await h.app.get(AdminAuthService).createAdmin(
        {
          email: 'mod@rwbr.test',
          displayName: 'Mod',
          password: 'moderator password 1',
          roles: ['MODERATOR'],
        },
        'system:test',
      );
      const login = await h
        .http()
        .post('/api/v1/admin/auth/login')
        .send({
          email: 'mod@rwbr.test',
          password: 'moderator password 1',
          code: totpAt(base32Decode(created.totpSecret), currentStep()),
        });
      adminToken = login.body.token;
    }, 60_000);
    afterAll(() => h?.close());

    const call = (method: string, url: string, auth?: [string, string]) => {
      const req = (h.http() as unknown as Record<string, (u: string) => import('supertest').Test>)[
        method
      ]!(url);
      if (auth) req.set(...auth);
      return method === 'get' || method === 'delete' ? req : req.send({});
    };

    it('covers every operation in the contract', () => {
      expect(operations.length).toBeGreaterThan(60);
      expect(new Set(operations.map((o) => o.scheme))).toEqual(
        new Set(['none', 'bearer', 'admin', 'serverSignature']),
      );
    });

    it.each(operations.filter((o) => o.scheme === 'bearer'))(
      '$method $path needs a player token',
      async ({ method, path: p }) => {
        expect((await call(method, concrete(p))).status).toBe(401);
        expect(
          (await call(method, concrete(p), ['Authorization', `Admin ${adminToken}`])).status,
        ).toBe(401);
        expect(
          (await call(method, concrete(p), ['Authorization', 'Bearer not.a.jwt'])).status,
        ).toBe(401);
      },
    );

    it.each(operations.filter((o) => o.scheme === 'admin'))(
      '$method $path needs an admin session',
      async ({ method, path: p }) => {
        expect((await call(method, concrete(p))).status).toBe(401);
        expect((await call(method, concrete(p), bearer(player.accessToken))).status).toBe(401);
      },
    );

    it.each(operations.filter((o) => o.scheme === 'serverSignature'))(
      '$method $path needs a server signature',
      async ({ method, path: p }) => {
        expect((await call(method, concrete(p))).status).toBe(401);
        expect((await call(method, concrete(p), bearer(player.accessToken))).status).toBe(401);
      },
    );

    it('rejects tampered and unsigned access tokens', async () => {
      const [header, payload, sig] = player.accessToken.split('.');
      const claims = JSON.parse(Buffer.from(payload!, 'base64url').toString());
      const forged = Buffer.from(JSON.stringify({ ...claims, sub: randomUUID() })).toString(
        'base64url',
      );
      expect(
        (
          await h
            .http()
            .get('/api/v1/player/profile')
            .set(...bearer(`${header}.${forged}.${sig}`))
        ).status,
      ).toBe(401);
      const none = Buffer.from(JSON.stringify({ alg: 'none', typ: 'JWT' })).toString('base64url');
      expect(
        (
          await h
            .http()
            .get('/api/v1/player/profile')
            .set(...bearer(`${none}.${payload}.`))
        ).status,
      ).toBe(401);
    });

    it('applies a ban to access tokens already issued', async () => {
      const victim = await guest(h);
      expect(
        (
          await h
            .http()
            .get('/api/v1/player/profile')
            .set(...bearer(victim.accessToken))
        ).status,
      ).toBe(200);
      const ban = await h
        .http()
        .post(`/api/v1/admin/players/${victim.userId}/sanctions`)
        .set('Authorization', `Admin ${adminToken}`)
        .send({ action: 'BAN', reason: 'confirmed aimbot' });
      expect(ban.status).toBe(201);
      const after = await h
        .http()
        .get('/api/v1/player/profile')
        .set(...bearer(victim.accessToken));
      expect(after.status).toBe(403);
      expect(after.body.error.code).toBe('ACCOUNT_RESTRICTED');
    });

    it('sends security headers on every response', async () => {
      const res = await h.http().get('/api/v1/health');
      expect(res.headers['x-content-type-options']).toBe('nosniff');
      expect(res.headers['content-security-policy']).toContain("default-src 'none'");
      expect(res.headers['cache-control']).toBe('no-store');
      expect(res.headers['x-powered-by']).toBeUndefined();
    });

    it('requeues players from a match its server never hosted', async () => {
      const db = h.app.get(Db);
      const g = await guest(h);
      const server = await db.one<{ id: string }>(
        `INSERT INTO game_servers (address, region, build_version) VALUES ('10.0.0.7:7777', 'IN-SOUTH', 't') RETURNING id`,
      );
      const m = await db.one<{ id: string }>(
        `INSERT INTO matches (game_server_id, map_version, mode, region, capacity, ruleset_version, created_at)
       VALUES ($1, 'greybox-block-100-v2', 'SOLO', 'IN-SOUTH', 3, 'v1', now() - interval '1 minute') RETURNING id`,
        [server!.id],
      );
      await db.query(
        `INSERT INTO matchmaking_queue (user_id, mode, region, client_version, match_id, matched_at)
       VALUES ($1, 'SOLO', 'IN-SOUTH', '0.6.0', $2, now())`,
        [g.userId, m!.id],
      );
      const status = await h
        .http()
        .get('/api/v1/matchmaking/status')
        .set(...bearer(g.accessToken));
      expect(status.status).toBe(200);
      expect(status.body.seat ?? null).toBeNull();
      const after = await db.one<{ status: string }>('SELECT status FROM matches WHERE id = $1', [
        m!.id,
      ]);
      expect(after!.status).toBe('CANCELLED');
      const queue = await db.one<{ match_id: string | null }>(
        'SELECT match_id FROM matchmaking_queue WHERE user_id = $1',
        [g.userId],
      );
      expect(queue!.match_id).not.toBe(m!.id);
    });
  },
);
