import { createHash, randomUUID } from 'node:crypto';
import { AdminAuthService } from '../admin/admin-auth';
import { base32Decode, currentStep, totpAt } from '../admin/admin-crypto';
import { Db } from '../database/database.module';
import { bearer, describeDb, expectContract, guest, Harness, startApi } from './harness';

describeDb('telemetry, KPIs and release gates (PostgreSQL)', () => {
  let h: Harness;
  let adminToken: string;

  beforeAll(async () => {
    h = await startApi(`rwbr_api_telemetry_${process.pid}`, { RATE_LIMIT_ENABLED: 'false' });
    const created = await h.app.get(AdminAuthService).createAdmin(
      {
        email: 'analyst@rwbr.test',
        displayName: 'Analyst',
        password: 'analyst password 1',
        roles: ['ANALYST'],
      },
      'system:test',
    );
    const login = await h
      .http()
      .post('/api/v1/admin/auth/login')
      .send({
        email: 'analyst@rwbr.test',
        password: 'analyst password 1',
        code: totpAt(base32Decode(created.totpSecret), currentStep()),
      });
    adminToken = login.body.token;
  }, 60_000);
  afterAll(() => h?.close());

  const batch = (
    events: object[],
    device = 'Pixel 7',
    token?: string,
    sessionId = randomUUID(),
  ) => {
    const req = h.http().post('/api/v1/telemetry/events');
    if (token) req.set(...bearer(token));
    return req.send({
      sessionId,
      appVersion: '0.6.0',
      platform: 'ANDROID',
      deviceModel: device,
      events,
    });
  };
  const now = () => new Date().toISOString();

  it('accepts valid batches with or without sign-in and rejects free-form data', async () => {
    const g = await guest(h);
    const signedIn = await batch(
      [{ type: 'SESSION_START', at: now(), data: { os: 'Android 14', quality: 'MEDIUM' } }],
      'Pixel 7',
      g.accessToken,
    );
    expect(signedIn.status).toBe(202);
    expectContract(signedIn, 'POST', '/telemetry/events');
    const anonymous = await batch([
      { type: 'CRASH', at: now(), data: { message: 'NullReferenceException at boot' } },
    ]);
    expect(anonymous.body).toEqual({ accepted: 1, dropped: 0 });

    const old = await batch([
      { type: 'SESSION_START', at: new Date(Date.now() - 9 * 86_400_000).toISOString(), data: {} },
    ]);
    expect(old.body).toEqual({ accepted: 0, dropped: 1 });
    const extra = await batch([
      { type: 'SESSION_START', at: now(), data: { email: 'someone@example.com' } },
    ]);
    expect(extra.status).toBe(422);
    const unknown = await batch([{ type: 'PURCHASE', at: now(), data: {} }]);
    expect(unknown.status).toBe(422);
    expectContract(unknown, 'POST', '/telemetry/events');

    const stored = await h.app
      .get(Db)
      .one<{ user_id: string | null }>(
        `SELECT user_id FROM telemetry_events WHERE event_type = 'SESSION_START' AND device_model = 'Pixel 7' LIMIT 1`,
      );
    expect(stored!.user_id).toBe(g.userId);
  });

  it('computes the beta KPIs and evaluates the release gates', async () => {
    const db = h.app.get(Db);
    // Sessions: 3 more starts, one of which crashes -> 1 crash-free failure among 4 sessions (with the earlier one).
    const crashed = randomUUID();
    await batch([{ type: 'SESSION_START', at: now(), data: {} }], 'Galaxy A14', undefined, crashed);
    await batch(
      [{ type: 'CRASH', at: now(), data: { message: 'OutOfMemory' } }],
      'Galaxy A14',
      undefined,
      crashed,
    );
    for (let i = 0; i < 2; i++)
      await batch([{ type: 'SESSION_START', at: now(), data: {} }], 'Galaxy A14');
    await batch(
      [
        {
          type: 'FPS_SAMPLE',
          at: now(),
          data: { avgFps: 30, p5Fps: 22, seconds: 60, scene: 'ONLINE', quality: 'LOW' },
        },
        {
          type: 'FPS_SAMPLE',
          at: now(),
          data: { avgFps: 60, p5Fps: 40, seconds: 180, scene: 'ONLINE', quality: 'LOW' },
        },
        { type: 'DISCONNECT', at: now(), data: { reason: 'timeout', reconnected: true } },
      ],
      'Galaxy A14',
    );

    // Matches: 19 completed + 1 cancelled after hosting -> 95% completion; queue times 10 s and 30 s.
    const server = await db.one<{ id: string }>(
      `INSERT INTO game_servers (address, region, build_version) VALUES ('10.0.0.9:7777', 'IN-SOUTH', 't') RETURNING id`,
    );
    for (let i = 0; i < 20; i++) {
      const done = i < 19;
      await db.query(
        `INSERT INTO matches (game_server_id, map_version, mode, region, capacity, ruleset_version, status, hosted_at, result_sha256)
         VALUES ($1, 'greybox-block-100-v2', 'SOLO', 'IN-SOUTH', 30, 'v1', $2, now(), $3)`,
        [
          server!.id,
          done ? 'COMPLETED' : 'CANCELLED',
          done ? createHash('sha256').update(String(i)).digest() : null,
        ],
      );
    }
    const players = [await guest(h), await guest(h)];
    const m = await db.one<{ id: string }>('SELECT id FROM matches LIMIT 1');
    for (const [i, p] of players.entries())
      await db.query(
        `INSERT INTO match_participants (match_id, user_id, enqueued_at) VALUES ($1, $2, now() - make_interval(secs => $3::int))`,
        [m!.id, p.userId, i === 0 ? 10 : 30],
      );

    // Retention: two players who signed up 2.5 days ago; one came back on day 1.
    const [back, gone] = [await guest(h), await guest(h)];
    for (const p of [back, gone])
      await db.query(`UPDATE users SET created_at = now() - interval '60 hours' WHERE id = $1`, [
        p.userId,
      ]);
    await db.query(
      `UPDATE player_sessions SET created_at = now() - interval '30 hours' WHERE user_id = $1`,
      [back.userId],
    );
    await db.query(
      `UPDATE player_sessions SET created_at = now() - interval '59 hours' WHERE user_id = $1`,
      [gone.userId],
    );

    const res = await h
      .http()
      .get('/api/v1/admin/metrics')
      .query({ days: 7 })
      .set('Authorization', `Admin ${adminToken}`);
    expect(res.status).toBe(200);
    expectContract(res, 'GET', '/admin/metrics');
    const k = res.body;
    expect(k.sessions).toEqual({ total: 4, crashed: 1, crashFreeRate: 0.75 });
    expect(k.fpsByDevice[0]).toEqual({
      platform: 'ANDROID',
      deviceModel: 'Galaxy A14',
      samples: 2,
      avgFps: 52.5,
      p5Fps: 31,
    });
    expect(k.matches).toEqual({ hosted: 20, completed: 19, completionRate: 0.95 });
    expect(k.disconnects).toMatchObject({ total: 1, reconnected: 1 });
    expect(k.queueSeconds.samples).toBe(2);
    expect(k.queueSeconds.p50).toBeCloseTo(20, 0);
    expect(k.retention.d1).toEqual({ cohort: 2, retained: 1, rate: 0.5 });
    const gates = Object.fromEntries(
      k.releaseGates.map((g: { key: string; status: string }) => [g.key, g.status]),
    );
    expect(gates).toEqual({
      crash_free_sessions: 'FAIL',
      match_completion: 'PASS',
      purchase_duplication: 'PASS',
      security_issues: 'MANUAL',
      legal_review: 'MANUAL',
      on_call: 'MANUAL',
    });

    const noPermission = await h
      .http()
      .get('/api/v1/admin/metrics')
      .set(...bearer(players[0]!.accessToken));
    expect(noPermission.status).toBe(401);
  });
});
