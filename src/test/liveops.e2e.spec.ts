import { createHmac, randomUUID } from 'node:crypto';
import { AdminAuthService } from '../admin/admin-auth';
import { base32Decode, currentStep, totpAt } from '../admin/admin-crypto';
import { Db } from '../database/database.module';
import { LatencyRecorder } from '../ops/latency.recorder';
import { addSeasonXp } from '../seasons/seasons.service';
import { devReceipt } from '../store/verifiers';
import {
  bearer,
  describeDb,
  DEV_PURCHASE_SECRET,
  expectContract,
  guest,
  Harness,
  signed,
  startApi,
} from './harness';

describeDb('live ops: season pass, ops dashboard, releases, economy (PostgreSQL)', () => {
  let h: Harness;
  const tokens: Record<string, string> = {};

  beforeAll(async () => {
    h = await startApi(`rwbr_api_liveops_${process.pid}`, {
      RATE_LIMIT_ENABLED: 'false',
      OPS_FLUSH_SECONDS: '0',
    });
    const auth = h.app.get(AdminAuthService);
    for (const [name, roles] of Object.entries({
      root: ['SUPER_ADMIN'],
      econ: ['ECONOMY_OPERATOR'],
    })) {
      const a = await auth.createAdmin(
        { email: `${name}@rwbr.test`, displayName: name, password: 'a long admin password', roles },
        'system:test',
      );
      const login = await h
        .http()
        .post('/api/v1/admin/auth/login')
        .send({
          email: `${name}@rwbr.test`,
          password: 'a long admin password',
          code: totpAt(base32Decode(a.totpSecret), currentStep()),
        });
      tokens[name] = login.body.token;
    }
  }, 60_000);
  afterAll(() => h?.close());

  const api = () => h.http();
  const as = (name: string): [string, string] => ['Authorization', `Admin ${tokens[name]}`];
  const iso = (offsetHours: number) => new Date(Date.now() + offsetHours * 3600_000).toISOString();

  it('runs a season: publish, earn XP, claim free and premium rewards, refund the pass', async () => {
    const season = {
      key: 'monsoon-1',
      title: 'Monsoon Season',
      startsAt: iso(-1),
      endsAt: iso(24 * 30),
      xpPerTier: 100,
      rewards: [
        { tier: 1, track: 'FREE', reward: { type: 'CURRENCY', currency: 'COIN', amount: 50 } },
        {
          tier: 1,
          track: 'PREMIUM',
          reward: { type: 'COSMETIC', cosmeticKey: 'skin_rifle_neon_01' },
        },
        { tier: 2, track: 'FREE', reward: { type: 'COSMETIC', cosmeticKey: 'banner_irani_chai' } },
      ],
    };
    const draft = await api()
      .post('/api/v1/admin/seasons')
      .set(...as('root'))
      .send(season);
    expect(draft.status).toBe(201);
    expectContract(draft, 'POST', '/admin/seasons');
    const published = await api()
      .post('/api/v1/admin/seasons/monsoon-1/publish')
      .set(...as('root'));
    expect(published.body.status).toBe('PUBLISHED');
    const frozen = await api()
      .post('/api/v1/admin/seasons')
      .set(...as('root'))
      .send({ ...season, title: 'Renamed' });
    expect(frozen.status).toBe(409);
    await api()
      .post('/api/v1/admin/seasons')
      .set(...as('root'))
      .send({ ...season, key: 'overlap-1' });
    const overlap = await api()
      .post('/api/v1/admin/seasons/overlap-1/publish')
      .set(...as('root'));
    expect(overlap.status).toBe(409);
    expect(overlap.body.error.code).toBe('SEASON_OVERLAP');

    // The premium pass is an ordinary store product: author + second approver.
    const pass = await api()
      .post('/api/v1/admin/catalog/products')
      .set(...as('econ'))
      .send({
        key: 'season_pass_monsoon',
        displayName: 'Monsoon Season Pass',
        price: { currency: 'INR', amountMinor: 19900 },
        grants: [{ type: 'SEASON_PASS', seasonKey: 'monsoon-1' }],
        skus: { ANDROID: 'rwbr.season_pass_monsoon' },
      });
    expect(pass.status).toBe(201);
    expectContract(pass, 'POST', '/admin/catalog/products');
    await api()
      .post('/api/v1/admin/catalog/products/season_pass_monsoon/submit')
      .set(...as('econ'));
    expect(
      (
        await api()
          .post('/api/v1/admin/catalog/products/season_pass_monsoon/approve')
          .set(...as('root'))
      ).body.status,
    ).toBe('PUBLISHED');

    const g = await guest(h);
    const claim = (tier: number, track: string) =>
      api()
        .post('/api/v1/seasons/current/claim')
        .set(...bearer(g.accessToken))
        .send({ tier, track });
    const start = await api()
      .get('/api/v1/seasons/current')
      .set(...bearer(g.accessToken));
    expectContract(start, 'GET', '/seasons/current');
    expect(start.body.season).toMatchObject({
      key: 'monsoon-1',
      maxTier: 2,
      premiumProductKey: 'season_pass_monsoon',
    });
    expect(start.body.progress).toEqual({ xp: 0, tier: 0, premium: false, xpToNextTier: 100 });
    expect((await claim(1, 'FREE')).body.error.code).toBe('TIER_LOCKED');

    // Season XP comes from match results (same transaction as the match reward).
    await h.app.get(Db).tx((c) => addSeasonXp(c, g.userId, 150));
    const ok = await claim(1, 'FREE');
    expect(ok.status).toBe(200);
    expectContract(ok, 'POST', '/seasons/current/claim');
    expect(
      ok.body.rewards.find(
        (r: { tier: number; track: string }) => r.tier === 1 && r.track === 'FREE',
      ).claimed,
    ).toBe(true);
    expect((await claim(1, 'FREE')).body.error.code).toBe('ALREADY_CLAIMED');
    expect((await claim(2, 'FREE')).body.error.code).toBe('TIER_LOCKED');
    expect((await claim(1, 'PREMIUM')).body.error.code).toBe('PREMIUM_REQUIRED');
    const inv = async () =>
      (
        await api()
          .get('/api/v1/inventory')
          .set(...bearer(g.accessToken))
      ).body;
    expect((await inv()).wallets.COIN).toBe(50);

    // Buy the pass through the dev store, claim the premium reward.
    const order = await api()
      .post('/api/v1/store/orders')
      .set(...bearer(g.accessToken))
      .send({ productKey: 'season_pass_monsoon', platform: 'DEV', idempotencyKey: 'pass-order-1' });
    const verify = await api()
      .post(`/api/v1/store/orders/${order.body.orderId}/verify`)
      .set(...bearer(g.accessToken))
      .send({
        proof: devReceipt(
          DEV_PURCHASE_SECRET,
          order.body.orderId,
          'season_pass_monsoon',
          'pass-tx-1',
        ),
      });
    expect(verify.body.status).toBe('FULFILLED');
    const again = await api()
      .post('/api/v1/store/orders')
      .set(...bearer(g.accessToken))
      .send({ productKey: 'season_pass_monsoon', platform: 'DEV', idempotencyKey: 'pass-order-2' });
    expect(again.body.error.code).toBe('ALREADY_OWNED');
    expect((await claim(1, 'PREMIUM')).status).toBe(200);
    expect((await inv()).items.map((i: { cosmeticKey: string }) => i.cosmeticKey)).toContain(
      'skin_rifle_neon_01',
    );

    // A refund removes premium access and what it unlocked; the free reward stays.
    const body = JSON.stringify({
      eventId: 'pass-refund-1',
      type: 'REFUND',
      transactionId: 'pass-tx-1',
    });
    const refund = await api()
      .post('/api/v1/webhooks/dev')
      .set('Content-Type', 'application/json')
      .set(
        'X-RWBR-Dev-Signature',
        createHmac('sha256', DEV_PURCHASE_SECRET).update(body).digest('hex'),
      )
      .send(body);
    expect(refund.body.outcome).toBe('APPLIED');
    const after = await api()
      .get('/api/v1/seasons/current')
      .set(...bearer(g.accessToken));
    expect(after.body.progress.premium).toBe(false);
    const items = (await inv()).items.map((i: { cosmeticKey: string }) => i.cosmeticKey);
    expect(items).not.toContain('skin_rifle_neon_01');
    expect((await inv()).wallets.COIN).toBe(50);

    const economy = await api()
      .get('/api/v1/admin/economy')
      .query({ days: 7 })
      .set(...as('econ'));
    expect(economy.status).toBe(200);
    expectContract(economy, 'GET', '/admin/economy');
    expect(economy.body.currencies.COIN.byReason).toContainEqual({
      reason: 'SEASON_REWARD',
      issued: 50,
      removed: 0,
      entries: 1,
    });
    expect(economy.body.seasons).toContainEqual({
      seasonKey: 'monsoon-1',
      players: 1,
      premiumPlayers: 0,
      rewardsClaimed: 2,
    });
  });

  it('shows API latency, server tick health, capacity and cost', async () => {
    const hb = await signed(
      h,
      'POST',
      '/internal/game-servers/heartbeat',
      {
        address: '127.0.0.1:47811',
        region: 'IN-SOUTH',
        buildVersion: 'test',
        mapVersions: ['greybox-block-100-v2'],
        state: 'IDLE',
        connectedPlayers: 0,
        tickMsP99: null,
      },
      '127.0.0.1:47811',
    );
    expect(hb.status).toBe(200);
    await h.app
      .get(Db)
      .query(
        `UPDATE server_load_samples SET connected_players = 24, hosting = true, tick_ms_p99 = 21.5`,
      );
    const g = await guest(h);
    for (let i = 0; i < 5; i++)
      await api()
        .get('/api/v1/player/profile')
        .set(...bearer(g.accessToken));
    await h.app.get(LatencyRecorder).flush();

    const res = await api()
      .get('/api/v1/admin/ops')
      .query({ hours: 1 })
      .set(...as('root'));
    expect(res.status).toBe(200);
    expectContract(res, 'GET', '/admin/ops');
    const route = res.body.api.routes.find(
      (r: { route: string }) => r.route === 'GET /api/v1/player/profile',
    );
    expect(route).toMatchObject({ requests: 5, serverErrors: 0 });
    expect(route.p95Ms).toBeGreaterThan(0);
    expect(res.body.servers).toMatchObject({
      distinctServers: 1,
      tickP99Worst: 21.5,
      minutesOverBudget: 1,
    });
    expect(res.body.capacity.peakConcurrentPlayers).toBe(24);
    expect(res.body.cost.serverCostPerHourInr).toBe(25);
    expect(res.body.cost.estimatedInr).toBeCloseTo(25 / 60, 2);
  });

  it('halts a release whose crash-free sessions fall below its threshold', async () => {
    const created = await api()
      .post('/api/v1/admin/releases')
      .set(...as('root'))
      .send({
        platform: 'ANDROID',
        appVersion: '0.7.0',
        rolloutPercent: 10,
        regions: ['IN'],
        minSessions: 10,
      });
    expect(created.status).toBe(201);
    expectContract(created, 'POST', '/admin/releases');
    const send = (events: object[], sessionId = randomUUID()) =>
      api().post('/api/v1/telemetry/events').send({
        sessionId,
        appVersion: '0.7.0',
        platform: 'ANDROID',
        deviceModel: 'Redmi 12',
        events,
      });
    const now = () => new Date().toISOString();
    for (let i = 0; i < 9; i++) await send([{ type: 'SESSION_START', at: now(), data: {} }]);
    await send([
      { type: 'SESSION_START', at: now(), data: {} },
      { type: 'CRASH', at: now(), data: { message: 'NullReferenceException in StoreScreen' } },
    ]);

    const report = await api()
      .post('/api/v1/admin/reconcile')
      .set(...as('root'));
    expectContract(report, 'POST', '/admin/reconcile');
    expect(report.body.releasesHalted).toBe(1);
    const list = await api()
      .get('/api/v1/admin/releases')
      .set(...as('root'));
    expectContract(list, 'GET', '/admin/releases');
    expect(list.body.items[0]).toMatchObject({
      status: 'HALTED',
      health: { sessions: 10, crashed: 1, crashFreeRate: 0.9 },
      haltReason: 'crash-free 90.00% < 99.50% after 10 sessions',
    });

    const resume = await api()
      .patch(`/api/v1/admin/releases/${created.body.id}`)
      .set(...as('root'))
      .send({ status: 'ROLLED_BACK', reason: 'halted in Play Console; fix in 0.7.1' });
    expect(resume.body.status).toBe('ROLLED_BACK');
    const audit = await api()
      .get('/api/v1/admin/audit')
      .query({ entityType: 'release' })
      .set(...as('root'));
    expect(audit.body.items.map((e: { action: string }) => e.action)).toEqual([
      'release.update',
      'release.auto_halt',
      'release.create',
    ]);
  });
});
