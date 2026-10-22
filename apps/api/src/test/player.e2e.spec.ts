import { bearer, describeDb, expectContract, guest, Harness, startApi } from './harness';

describeDb('player, maps, inventory, reports (PostgreSQL)', () => {
  let h: Harness;
  beforeAll(async () => {
    h = await startApi(`rwbr_api_player_${process.pid}`, { RATE_LIMIT_ENABLED: 'false' });
  }, 60_000);
  afterAll(() => h?.close());

  it('updates the display name and validates it', async () => {
    const g = await guest(h);
    const ok = await h
      .http()
      .patch('/api/v1/player/profile')
      .set(...bearer(g.accessToken))
      .send({ displayName: 'Ravi_07' });
    expect(ok.status).toBe(200);
    expectContract(ok, 'PATCH', '/player/profile');
    expect(ok.body.displayName).toBe('Ravi_07');

    const bad = await h
      .http()
      .patch('/api/v1/player/profile')
      .set(...bearer(g.accessToken))
      .send({ displayName: '<script>' });
    expect(bad.status).toBe(422);
    expectContract(bad, 'PATCH', '/player/profile');
  });

  it('starts with zero stats and an empty history page', async () => {
    const g = await guest(h);
    const stats = await h
      .http()
      .get('/api/v1/player/stats')
      .set(...bearer(g.accessToken));
    expectContract(stats, 'GET', '/player/stats');
    expect(stats.body).toEqual({
      matchesPlayed: 0,
      wins: 0,
      kills: 0,
      winRate: 0,
      level: 1,
      xp: 0,
    });

    const page = await h
      .http()
      .get('/api/v1/player/matches?limit=5')
      .set(...bearer(g.accessToken));
    expectContract(page, 'GET', '/player/matches');
    expect(page.body).toEqual({ items: [], nextCursor: null });

    const badCursor = await h
      .http()
      .get('/api/v1/player/matches?cursor=nope')
      .set(...bearer(g.accessToken));
    expect(badCursor.status).toBe(422);
  });

  it('lists published maps and serves the latest manifest', async () => {
    const list = await h.http().get('/api/v1/maps');
    expectContract(list, 'GET', '/maps');
    expect(list.body.items.map((m: { version: string }) => m.version)).toEqual(
      expect.arrayContaining(['greybox-block-100-v2', 'greybox-block-40-v2']),
    );
    const manifest = await h.http().get('/api/v1/maps/greybox-block/manifest');
    expectContract(manifest, 'GET', '/maps/{mapKey}/manifest');
    const missing = await h.http().get('/api/v1/maps/nowhere/manifest');
    expect(missing.status).toBe(404);
    expectContract(missing, 'GET', '/maps/{mapKey}/manifest');
  });

  it('gives starter cosmetics, equips owned items only, and starts wallets at zero', async () => {
    const g = await guest(h);
    const inv = await h
      .http()
      .get('/api/v1/inventory')
      .set(...bearer(g.accessToken));
    expectContract(inv, 'GET', '/inventory');
    expect(inv.body.items).toHaveLength(3);
    expect(inv.body.loadout).toMatchObject({
      WEAPON_SKIN: 'skin_rifle_default',
      OUTFIT: 'outfit_default',
      BANNER: 'banner_default',
    });
    expect(inv.body.wallets).toEqual({ COIN: 0, GEM: 0 });

    const wrongSlot = await h
      .http()
      .put('/api/v1/inventory/loadout/EMOTE')
      .set(...bearer(g.accessToken))
      .send({ cosmeticKey: 'outfit_default' });
    expect(wrongSlot.status).toBe(422);
    const notOwned = await h
      .http()
      .put('/api/v1/inventory/loadout/WEAPON_SKIN')
      .set(...bearer(g.accessToken))
      .send({ cosmeticKey: 'skin_rifle_neon_01' });
    expect(notOwned.status).toBe(403);
    expectContract(notOwned, 'PUT', '/inventory/loadout/{slot}');
    const ok = await h
      .http()
      .put('/api/v1/inventory/loadout/OUTFIT')
      .set(...bearer(g.accessToken))
      .send({ cosmeticKey: 'outfit_default' });
    expect(ok.status).toBe(200);
    expectContract(ok, 'PUT', '/inventory/loadout/{slot}');
  });

  it('files reports, hides them from other players, and refuses self-reports', async () => {
    const reporter = await guest(h),
      cheater = await guest(h),
      other = await guest(h);
    const filed = await h
      .http()
      .post('/api/v1/reports')
      .set(...bearer(reporter.accessToken))
      .send({
        reportedPlayerId: cheater.userId,
        reason: 'CHEATING',
        details: 'wall hacks near the market',
      });
    expect(filed.status).toBe(201);
    expectContract(filed, 'POST', '/reports');
    expect(filed.body.status).toBe('OPEN');

    const mine = await h
      .http()
      .get(`/api/v1/reports/${filed.body.reportId}`)
      .set(...bearer(reporter.accessToken));
    expectContract(mine, 'GET', '/reports/{reportId}');
    const theirs = await h
      .http()
      .get(`/api/v1/reports/${filed.body.reportId}`)
      .set(...bearer(other.accessToken));
    expect(theirs.status).toBe(404);

    const self = await h
      .http()
      .post('/api/v1/reports')
      .set(...bearer(reporter.accessToken))
      .send({ reportedPlayerId: reporter.userId, reason: 'OTHER' });
    expect(self.status).toBe(422);
  });
});
