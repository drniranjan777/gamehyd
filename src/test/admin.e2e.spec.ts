import { AdminAuthService } from '../admin/admin-auth';
import { base32Decode, currentStep, totpAt } from '../admin/admin-crypto';
import { Db } from '../database/database.module';
import { credit } from '../matchmaking/results.service';
import { bearer, describeDb, expectContract, guest, Harness, startApi } from './harness';

const PASSWORD = 'correct horse battery staple';

describeDb('admin API: MFA, RBAC, two-person catalogue, moderation, audit (PostgreSQL)', () => {
  let h: Harness;
  const admins: Record<string, { email: string; secret: Buffer; token?: string; id?: string }> = {};
  let step = currentStep();

  beforeAll(async () => {
    h = await startApi(`rwbr_api_admin_${process.pid}`, { RATE_LIMIT_ENABLED: 'false' });
    const auth = h.app.get(AdminAuthService);
    const roles: Record<string, string[]> = {
      root: ['SUPER_ADMIN'],
      econ: ['ECONOMY_OPERATOR'],
      support: ['SUPPORT'],
      mod: ['MODERATOR'],
      locked: ['ANALYST'],
    };
    for (const [name, r] of Object.entries(roles)) {
      const created = await auth.createAdmin(
        { email: `${name}@rwbr.test`, displayName: name, password: PASSWORD, roles: r },
        'system:test',
      );
      admins[name] = {
        email: `${name}@rwbr.test`,
        secret: base32Decode(created.totpSecret),
        id: created.admin.id,
      };
    }
    for (const name of ['root', 'econ', 'support', 'mod'])
      admins[name]!.token = (await login(name)).body.token;
  }, 60_000);
  afterAll(() => h?.close());

  const api = () => h.http();
  /** Logs in with the current TOTP code. Each admin's codes are single-use, so step forward within the ±1 window. */
  function login(name: string, password = PASSWORD, code?: string) {
    const a = admins[name]!;
    return api()
      .post('/api/v1/admin/auth/login')
      .send({ email: a.email, password, code: code ?? totpAt(a.secret, step) });
  }
  const as = (name: string) =>
    ['Authorization', `Admin ${admins[name]!.token}`] as [string, string];

  it('requires password and a single-use TOTP code', async () => {
    const a = admins.root!;
    const replay = await login('root', PASSWORD, totpAt(a.secret, step));
    expect(replay.status).toBe(401);
    expectContract(replay, 'POST', '/admin/auth/login');
    const wrongPassword = await login('root', 'not the password', totpAt(a.secret, step + 1));
    expect(wrongPassword.status).toBe(401);
    const wrongCode = await login('root', PASSWORD, '000000');
    expect(wrongCode.status).toBe(401);
    const unknown = await api()
      .post('/api/v1/admin/auth/login')
      .send({ email: 'nobody@rwbr.test', password: PASSWORD, code: '123456' });
    expect(unknown.status).toBe(401);
    expect(unknown.body.error.code).toBe(wrongPassword.body.error.code);

    const me = await api()
      .get('/api/v1/admin/me')
      .set(...as('root'));
    expect(me.status).toBe(200);
    expectContract(me, 'GET', '/admin/me');
    expect(me.body.permissions).toContain('admins.manage');
    expect((await api().get('/api/v1/admin/me')).status).toBe(401);
    expect(
      (await api().get('/api/v1/admin/me').set('Authorization', 'Admin not-a-real-token-at-all-xx'))
        .status,
    ).toBe(401);
  });

  it('locks the account after five failures', async () => {
    for (let i = 0; i < 5; i++) expect((await login('locked', 'wrong password')).status).toBe(401);
    const locked = await login('locked');
    expect(locked.status).toBe(423);
    expect(locked.body.error.code).toBe('ACCOUNT_LOCKED');
    expectContract(locked, 'POST', '/admin/auth/login');
  });

  it('enforces permissions per role', async () => {
    const g = await guest(h, 'Searchable_Sam');
    const found = await api()
      .get('/api/v1/admin/players')
      .query({ q: 'Searchable' })
      .set(...as('support'));
    expect(found.status).toBe(200);
    expectContract(found, 'GET', '/admin/players');
    expect(found.body.items[0].playerId).toBe(g.userId);

    const detail = await api()
      .get(`/api/v1/admin/players/${g.userId}`)
      .set(...as('support'));
    expectContract(detail, 'GET', '/admin/players/{playerId}');

    const denied = await api()
      .post('/api/v1/admin/catalog/products')
      .set(...as('support'))
      .send({});
    expect(denied.status).toBe(403);
    expect(denied.body.error.code).toBe('PERMISSION_DENIED');
    const noSanction = await api()
      .post(`/api/v1/admin/players/${g.userId}/sanctions`)
      .set(...as('econ'))
      .send({ action: 'WARN', reason: 'testing permissions' });
    expect(noSanction.status).toBe(403);

    const overview = await api()
      .get('/api/v1/admin/overview')
      .set(...as('support'));
    expect(overview.status).toBe(200);
    expectContract(overview, 'GET', '/admin/overview');
  });

  it('publishes a product only after a second person approves it', async () => {
    const draft = {
      key: 'skin_rifle_diwali_lights',
      displayName: 'Festival Lights rifle skin',
      price: { currency: 'INR', amountMinor: 9900 },
      grants: [{ type: 'COSMETIC', cosmeticKey: 'skin_rifle_neon_01' }],
    };
    const created = await api()
      .post('/api/v1/admin/catalog/products')
      .set(...as('econ'))
      .send(draft);
    expect(created.status).toBe(201);
    expectContract(created, 'POST', '/admin/catalog/products');
    expect(created.body.status).toBe('DRAFT');

    const coins = await api()
      .post('/api/v1/admin/catalog/products')
      .set(...as('econ'))
      .send({
        ...draft,
        key: 'coin_pack_bad',
        grants: [{ type: 'CURRENCY', currency: 'COIN', amount: 1000 }],
      });
    expect(coins.status).toBe(422);

    const noSku = await api()
      .post('/api/v1/admin/catalog/products/skin_rifle_diwali_lights/submit')
      .set(...as('econ'));
    expect(noSku.status).toBe(422);
    const patched = await api()
      .patch('/api/v1/admin/catalog/products/skin_rifle_diwali_lights')
      .set(...as('econ'))
      .send({ skus: { ANDROID: 'rwbr.skin_rifle_diwali_lights' } });
    expectContract(patched, 'PATCH', '/admin/catalog/products/{key}');
    const submitted = await api()
      .post('/api/v1/admin/catalog/products/skin_rifle_diwali_lights/submit')
      .set(...as('econ'));
    expect(submitted.body.status).toBe('PENDING_APPROVAL');

    const self = await api()
      .post('/api/v1/admin/catalog/products/skin_rifle_diwali_lights/approve')
      .set(...as('econ'));
    expect(self.status).toBe(403);
    expect(self.body.error.code).toBe('SELF_APPROVAL');
    const approved = await api()
      .post('/api/v1/admin/catalog/products/skin_rifle_diwali_lights/approve')
      .set(...as('root'));
    expect(approved.status).toBe(200);
    expectContract(approved, 'POST', '/admin/catalog/products/{key}/approve');
    expect(approved.body).toMatchObject({
      status: 'PUBLISHED',
      approvedBy: `admin:${admins.root!.id}`,
    });

    const frozen = await api()
      .patch('/api/v1/admin/catalog/products/skin_rifle_diwali_lights')
      .set(...as('econ'))
      .send({ price: { currency: 'INR', amountMinor: 100 } });
    expect(frozen.status).toBe(409);

    const g = await guest(h);
    const cat = await api()
      .get('/api/v1/store/catalog')
      .set(...bearer(g.accessToken));
    expect(cat.body.items.map((i: { productKey: string }) => i.productKey)).toContain(
      'skin_rifle_diwali_lights',
    );

    const retired = await api()
      .post('/api/v1/admin/catalog/products/skin_rifle_diwali_lights/retire')
      .set(...as('econ'))
      .send({ reason: 'festival over' });
    expect(retired.body.status).toBe('RETIRED');
    const after = await api()
      .get('/api/v1/store/catalog')
      .set(...bearer(g.accessToken));
    expect(after.body.items.map((i: { productKey: string }) => i.productKey)).not.toContain(
      'skin_rifle_diwali_lights',
    );

    const trail = await api()
      .get('/api/v1/admin/audit')
      .query({ entityType: 'product', entityId: 'skin_rifle_diwali_lights' })
      .set(...as('root'));
    expectContract(trail, 'GET', '/admin/audit');
    expect(trail.body.items.map((e: { action: string }) => e.action)).toEqual([
      'product.retire',
      'product.approve',
      'product.submit',
      'product.update',
      'product.create',
    ]);
  });

  it('suspends and bans players, ending their sessions, and lifts sanctions', async () => {
    const g = await guest(h);
    const sanction = (body: object) =>
      api()
        .post(`/api/v1/admin/players/${g.userId}/sanctions`)
        .set(...as('mod'))
        .send(body);

    const noExpiry = await sanction({ action: 'SUSPEND', reason: 'aimbot suspicion' });
    expect(noExpiry.status).toBe(422);
    const suspended = await sanction({
      action: 'SUSPEND',
      reason: 'aimbot suspicion',
      expiresAt: new Date(Date.now() + 86400_000).toISOString(),
    });
    expect(suspended.status).toBe(201);
    expectContract(suspended, 'POST', '/admin/players/{playerId}/sanctions');
    expect(suspended.body.playerStatus).toBe('SUSPENDED');

    const refresh = await api()
      .post('/api/v1/auth/token/refresh')
      .send({ refreshToken: g.refreshToken });
    expect(refresh.status).toBe(401);
    const signIn = await api().post('/api/v1/auth/guest').send(g.guest);
    expect(signIn.status).toBe(403);
    expect(signIn.body.error.code).toBe('ACCOUNT_RESTRICTED');

    const lifted = await sanction({ action: 'UNBAN', reason: 'appeal accepted' });
    expect(lifted.body.playerStatus).toBe('ACTIVE');
    expect((await api().post('/api/v1/auth/guest').send(g.guest)).status).toBe(200);
    const again = await sanction({ action: 'UNBAN', reason: 'appeal accepted' });
    expect(again.status).toBe(409);

    const detail = await api()
      .get(`/api/v1/admin/players/${g.userId}`)
      .set(...as('mod'));
    expect(detail.body.sanctions.map((s: { action: string }) => s.action)).toEqual([
      'UNBAN',
      'SUSPEND',
    ]);
  });

  it('resolves reports from the moderation queue', async () => {
    const reporter = await guest(h);
    const target = await guest(h);
    await api()
      .post('/api/v1/reports')
      .set(...bearer(reporter.accessToken))
      .send({ reportedPlayerId: target.userId, reason: 'CHEATING' });
    const queue = await api()
      .get('/api/v1/admin/reports')
      .query({ status: 'OPEN' })
      .set(...as('mod'));
    expectContract(queue, 'GET', '/admin/reports');
    const report = queue.body.items.find(
      (r: { reportedPlayerId: string }) => r.reportedPlayerId === target.userId,
    );
    const resolved = await api()
      .patch(`/api/v1/admin/reports/${report.reportId}`)
      .set(...as('mod'))
      .send({ status: 'DISMISSED', note: 'no evidence in replay' });
    expect(resolved.status).toBe(200);
    expectContract(resolved, 'PATCH', '/admin/reports/{reportId}');
  });

  it('refunds an order, returning coins and revoking the item, once', async () => {
    const g = await guest(h);
    await h.app
      .get(Db)
      .tx((c) => credit(c, g.userId, 'COIN', 500, 'ADMIN_ADJUSTMENT', 'test', `grant-${g.userId}`));
    const o = await api()
      .post('/api/v1/store/orders')
      .set(...bearer(g.accessToken))
      .send({
        productKey: 'emote_victory_wave',
        platform: 'IN_GAME',
        idempotencyKey: 'admin-refund-01',
      });
    expect(o.body.status).toBe('FULFILLED');

    const support = await api()
      .post(`/api/v1/admin/orders/${o.body.orderId}/refund`)
      .set(...as('support'))
      .send({ reason: 'bought by mistake' });
    expect(support.status).toBe(403);
    const refund = await api()
      .post(`/api/v1/admin/orders/${o.body.orderId}/refund`)
      .set(...as('econ'))
      .send({ reason: 'bought by mistake' });
    expect(refund.status).toBe(200);
    expectContract(refund, 'POST', '/admin/orders/{orderId}/refund');
    expect(refund.body).toMatchObject({ status: 'REFUNDED', alreadyRefunded: false, shortfall: 0 });
    const twice = await api()
      .post(`/api/v1/admin/orders/${o.body.orderId}/refund`)
      .set(...as('econ'))
      .send({ reason: 'bought by mistake' });
    expect(twice.body.alreadyRefunded).toBe(true);

    const inv = (
      await api()
        .get('/api/v1/inventory')
        .set(...bearer(g.accessToken))
    ).body;
    expect(inv.wallets.COIN).toBe(500);
    expect(inv.items.map((i: { cosmeticKey: string }) => i.cosmeticKey)).not.toContain(
      'emote_victory_wave',
    );

    const detail = await api()
      .get(`/api/v1/admin/orders/${o.body.orderId}`)
      .set(...as('support'));
    expectContract(detail, 'GET', '/admin/orders/{orderId}');
    const list = await api()
      .get('/api/v1/admin/orders')
      .query({ playerId: g.userId })
      .set(...as('support'));
    expectContract(list, 'GET', '/admin/orders');
    expect(list.body.items).toHaveLength(1);

    const report = await api()
      .post('/api/v1/admin/reconcile')
      .set(...as('econ'));
    expectContract(report, 'POST', '/admin/reconcile');
    expect(report.body.walletsWithLedgerDrift).toBe(0);
  });

  it('publishes kill switches with optimistic concurrency and schedules events', async () => {
    const current = await api()
      .get('/api/v1/admin/liveops/config')
      .set(...as('support'));
    expectContract(current, 'GET', '/admin/liveops/config');
    const version = current.body.current.version;
    const config = { ...current.body.current.config, storeEnabled: false };

    const denied = await api()
      .put('/api/v1/admin/liveops/config')
      .set(...as('econ'))
      .send({ expectedVersion: version, config, reason: 'test' });
    expect(denied.status).toBe(403);
    const ok = await api()
      .put('/api/v1/admin/liveops/config')
      .set(...as('root'))
      .send({ expectedVersion: version, config, reason: 'payment incident' });
    expect(ok.status).toBe(200);
    expectContract(ok, 'PUT', '/admin/liveops/config');
    expect(ok.body.current.version).toBe(version + 1);
    const stale = await api()
      .put('/api/v1/admin/liveops/config')
      .set(...as('root'))
      .send({ expectedVersion: version, config, reason: 'payment incident' });
    expect(stale.status).toBe(409);
    expect((await api().get('/api/v1/config')).body.storeEnabled).toBe(false);
    await api()
      .put('/api/v1/admin/liveops/config')
      .set(...as('root'))
      .send({
        expectedVersion: version + 1,
        config: { ...config, storeEnabled: true },
        reason: 'incident resolved',
      });

    const ev = await api()
      .post('/api/v1/admin/liveops/events')
      .set(...as('root'))
      .send({
        key: 'bonfire-night',
        title: 'Bonfire Night',
        startsAt: new Date(Date.now() - 60_000).toISOString(),
        endsAt: new Date(Date.now() + 3600_000).toISOString(),
      });
    expect(ev.status).toBe(201);
    expectContract(ev, 'POST', '/admin/liveops/events');
    const pub = await api().get('/api/v1/events');
    expect(pub.body.items).toEqual([expect.objectContaining({ key: 'bonfire-night', live: true })]);
    const cancelled = await api()
      .post('/api/v1/admin/liveops/events/bonfire-night/cancel')
      .set(...as('root'))
      .send({ reason: 'weather' });
    expect(cancelled.body.status).toBe('CANCELLED');
    expect((await api().get('/api/v1/events')).body.items).toEqual([]);
  });

  it('manages admins: create with TOTP enrolment, change roles, disable', async () => {
    const created = await api()
      .post('/api/v1/admin/admins')
      .set(...as('root'))
      .send({
        email: 'new.analyst@rwbr.test',
        displayName: 'New Analyst',
        password: PASSWORD,
        roles: ['ANALYST'],
      });
    expect(created.status).toBe(201);
    expectContract(created, 'POST', '/admin/admins');
    expect(created.body.otpauthUrl).toMatch(/^otpauth:\/\/totp\//);
    admins.analyst = {
      email: 'new.analyst@rwbr.test',
      secret: base32Decode(created.body.totpSecret),
      id: created.body.admin.id,
    };
    const session = await login('analyst');
    expect(session.status).toBe(200);
    admins.analyst.token = session.body.token;

    const roles = await api()
      .put(`/api/v1/admin/admins/${admins.analyst.id}/roles`)
      .set(...as('root'))
      .send({ roles: ['SUPPORT'], reason: 'moved team' });
    expectContract(roles, 'PUT', '/admin/admins/{adminId}/roles');
    expect(roles.body.roles).toEqual(['SUPPORT']);
    const self = await api()
      .post(`/api/v1/admin/admins/${admins.root!.id}/disable`)
      .set(...as('root'))
      .send({ reason: 'oops' });
    expect(self.status).toBe(409);
    const disabled = await api()
      .post(`/api/v1/admin/admins/${admins.analyst.id}/disable`)
      .set(...as('root'))
      .send({ reason: 'left the company' });
    expect(disabled.status).toBe(200);
    expect(
      (
        await api()
          .get('/api/v1/admin/me')
          .set(...as('analyst'))
      ).status,
    ).toBe(401);

    const list = await api()
      .get('/api/v1/admin/admins')
      .set(...as('root'));
    expectContract(list, 'GET', '/admin/admins');
    const logout = await api()
      .post('/api/v1/admin/auth/logout')
      .set(...as('support'));
    expect(logout.status).toBe(204);
    expect(
      (
        await api()
          .get('/api/v1/admin/me')
          .set(...as('support'))
      ).status,
    ).toBe(401);
  });

  afterEach(() => {
    step = currentStep();
  });
});
