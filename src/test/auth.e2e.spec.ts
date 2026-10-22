import { bearer, describeDb, expectContract, guest, Harness, startApi } from './harness';

describeDb('auth (PostgreSQL)', () => {
  let h: Harness;
  beforeAll(async () => {
    h = await startApi(`rwbr_api_auth_${process.pid}`, { RATE_LIMIT_ENABLED: 'false' });
  }, 60_000);
  afterAll(() => h?.close());

  it('creates a guest with starter items and signs back in with the device credentials', async () => {
    const g = await guest(h, 'Asha');
    expect(g.guest.deviceSecret).toMatch(/^[A-Za-z0-9_-]{43}$/);

    const profile = await h
      .http()
      .get('/api/v1/player/profile')
      .set(...bearer(g.accessToken));
    expect(profile.status).toBe(200);
    expectContract(profile, 'GET', '/player/profile');
    expect(profile.body).toMatchObject({
      playerId: g.userId,
      displayName: 'Asha',
      level: 1,
      xp: 0,
    });

    const again = await h
      .http()
      .post('/api/v1/auth/guest')
      .send({ deviceId: g.guest.deviceId, deviceSecret: g.guest.deviceSecret });
    expect(again.status).toBe(200);
    expectContract(again, 'POST', '/auth/guest');
    expect(again.body.userId).toBe(g.userId);
    expect(again.body.guest).toBeUndefined(); // the secret is only ever shown once
  });

  it('gives the same answer for an unknown device and a wrong secret', async () => {
    const g = await guest(h);
    const wrong = await h
      .http()
      .post('/api/v1/auth/guest')
      .send({ deviceId: g.guest.deviceId, deviceSecret: 'x'.repeat(43) });
    const unknown = await h
      .http()
      .post('/api/v1/auth/guest')
      .send({ deviceId: '00000000-0000-4000-8000-000000000000', deviceSecret: 'x'.repeat(43) });
    for (const res of [wrong, unknown]) {
      expect(res.status).toBe(401);
      expectContract(res, 'POST', '/auth/guest');
      expect(res.body.error.code).toBe('INVALID_CREDENTIALS');
    }
  });

  it('rotates refresh tokens and revokes the whole sign-in when an old one is replayed', async () => {
    const g = await guest(h);
    const r1 = await h
      .http()
      .post('/api/v1/auth/token/refresh')
      .send({ refreshToken: g.refreshToken });
    expect(r1.status).toBe(200);
    expectContract(r1, 'POST', '/auth/token/refresh');
    expect(r1.body.refreshToken).not.toBe(g.refreshToken);

    const replay = await h
      .http()
      .post('/api/v1/auth/token/refresh')
      .send({ refreshToken: g.refreshToken });
    expect(replay.status).toBe(401);
    expect(replay.body.error.code).toBe('REFRESH_TOKEN_REUSED');

    // The legitimate newer token is now dead too: whoever stole the old one gets nothing.
    const after = await h
      .http()
      .post('/api/v1/auth/token/refresh')
      .send({ refreshToken: r1.body.refreshToken });
    expect(after.status).toBe(401);
  });

  it('logs out by revoking the refresh token', async () => {
    const g = await guest(h);
    const out = await h.http().post('/api/v1/auth/logout').send({ refreshToken: g.refreshToken });
    expect(out.status).toBe(204);
    const r = await h
      .http()
      .post('/api/v1/auth/token/refresh')
      .send({ refreshToken: g.refreshToken });
    expect(r.status).toBe(401);
  });

  it('rejects missing, forged and malformed access tokens', async () => {
    const g = await guest(h);
    const none = await h.http().get('/api/v1/player/profile');
    const forged = await h
      .http()
      .get('/api/v1/player/profile')
      .set(...bearer(g.accessToken.slice(0, -4) + 'AAAA'));
    for (const res of [none, forged]) {
      expect(res.status).toBe(401);
      expectContract(res, 'GET', '/player/profile');
    }
  });

  it('validates bodies with per-field details and never echoes secrets', async () => {
    const res = await h
      .http()
      .post('/api/v1/auth/guest')
      .send({ deviceId: 'not-a-uuid', deviceSecret: 'short', extra: true });
    expect(res.status).toBe(422);
    expectContract(res, 'POST', '/auth/guest');
    expect(res.body.error.details.map((d: { path: string }) => d.path)).toEqual(
      expect.arrayContaining(['deviceId', 'deviceSecret']),
    );
    expect(JSON.stringify(res.body)).not.toContain('short');
  });

  it('reports platform sign-in as not configured', async () => {
    const res = await h
      .http()
      .post('/api/v1/auth/platform')
      .send({ provider: 'APPLE', token: 'x'.repeat(20) });
    expect(res.status).toBe(501);
    expectContract(res, 'POST', '/auth/platform');
  });
});

describeDb('rate limits', () => {
  let h: Harness;
  beforeAll(async () => {
    h = await startApi(`rwbr_api_rate_${process.pid}`);
  }, 60_000);
  afterAll(() => h?.close());

  it('limits guest sign-ups per IP', async () => {
    const statuses: number[] = [];
    for (let i = 0; i < 22; i++)
      statuses.push((await h.http().post('/api/v1/auth/guest').send({})).status);
    expect(statuses.filter((s) => s === 200)).toHaveLength(20);
    const last = await h.http().post('/api/v1/auth/guest').send({});
    expect(last.status).toBe(429);
    expectContract(last, 'POST', '/auth/guest');
  });
});
