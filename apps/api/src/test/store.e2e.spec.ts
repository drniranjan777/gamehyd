import { createHmac } from 'node:crypto';
import { Db } from '../database/database.module';
import { credit } from '../matchmaking/results.service';
import { GameConfigService } from '../store/game-config.service';
import { JobsService } from '../store/jobs.service';
import { debit } from '../store/ledger';
import { devReceipt } from '../store/verifiers';
import {
  bearer,
  describeDb,
  DEV_PURCHASE_SECRET,
  expectContract,
  guest,
  Harness,
  startApi,
} from './harness';

describeDb('store, dev purchases, refunds, account deletion (PostgreSQL)', () => {
  let h: Harness;
  beforeAll(async () => {
    h = await startApi(`rwbr_api_store_${process.pid}`, { RATE_LIMIT_ENABLED: 'false' });
  }, 60_000);
  afterAll(() => h?.close());

  const api = () => h.http();
  const grantCoins = (userId: string, amount: number, key: string) =>
    h.app.get(Db).tx((c) => credit(c, userId, 'COIN', amount, 'ADMIN_ADJUSTMENT', 'test', key));
  const inventory = async (token: string) =>
    (
      await api()
        .get('/api/v1/inventory')
        .set(...bearer(token))
    ).body;
  const order = (token: string, productKey: string, platform: string, idempotencyKey: string) =>
    api()
      .post('/api/v1/store/orders')
      .set(...bearer(token))
      .send({ productKey, platform, idempotencyKey });
  const verify = (token: string, orderId: string, proof: string) =>
    api()
      .post(`/api/v1/store/orders/${orderId}/verify`)
      .set(...bearer(token))
      .send({ proof });
  const devHook = (body: object, secret = DEV_PURCHASE_SECRET) => {
    const raw = JSON.stringify(body);
    return api()
      .post('/api/v1/webhooks/dev')
      .set('Content-Type', 'application/json')
      .set('X-RWBR-Dev-Signature', createHmac('sha256', secret).update(raw).digest('hex'))
      .send(raw);
  };
  /** Buys an INR product through the dev platform; returns the order id. */
  async function devBuy(token: string, productKey: string, tx: string) {
    const o = await order(token, productKey, 'DEV', `buy-${tx}`);
    expect(o.status).toBe(201);
    const v = await verify(
      token,
      o.body.orderId,
      devReceipt(DEV_PURCHASE_SECRET, o.body.orderId, productKey, tx),
    );
    expect(v.status).toBe(200);
    expect(v.body.status).toBe('FULFILLED');
    return o.body.orderId as string;
  }

  it('lists the catalogue with server prices and ownership', async () => {
    const g = await guest(h);
    const res = await api()
      .get('/api/v1/store/catalog')
      .set(...bearer(g.accessToken));
    expect(res.status).toBe(200);
    expectContract(res, 'GET', '/store/catalog');
    expect(res.body.storeEnabled).toBe(true);
    const keys = res.body.items.map((i: { productKey: string }) => i.productKey);
    expect(keys).toEqual(
      expect.arrayContaining(['skin_rifle_neon_01', 'skin_rifle_charminar_gold', 'gems_100']),
    );
    const gold = res.body.items.find(
      (i: { productKey: string }) => i.productKey === 'skin_rifle_charminar_gold',
    );
    expect(gold).toMatchObject({ price: { currency: 'INR', amountMinor: 7900 }, owned: false });
    expect(gold.skus).toEqual({
      ANDROID: 'rwbr.skin_rifle_charminar_gold',
      IOS: 'rwbr.skin_rifle_charminar_gold',
    });
  });

  it('buys with coins atomically and idempotently', async () => {
    const g = await guest(h);
    const poor = await order(g.accessToken, 'skin_rifle_neon_01', 'IN_GAME', 'coin-buy-0001');
    expect(poor.status).toBe(409);
    expect(poor.body.error.code).toBe('INSUFFICIENT_FUNDS');
    expectContract(poor, 'POST', '/store/orders');

    await grantCoins(g.userId, 1000, `grant-${g.userId}`);
    const buy = await order(g.accessToken, 'skin_rifle_neon_01', 'IN_GAME', 'coin-buy-0001');
    expect(buy.status).toBe(201);
    expectContract(buy, 'POST', '/store/orders');
    expect(buy.body).toMatchObject({
      status: 'FULFILLED',
      sku: null,
      price: { currency: 'COIN', amountMinor: 400 },
    });

    const again = await order(g.accessToken, 'skin_rifle_neon_01', 'IN_GAME', 'coin-buy-0001');
    expect(again.body.orderId).toBe(buy.body.orderId);
    const inv = await inventory(g.accessToken);
    expect(inv.wallets.COIN).toBe(600);
    expect(inv.items.map((i: { cosmeticKey: string }) => i.cosmeticKey)).toContain(
      'skin_rifle_neon_01',
    );

    const owned = await order(g.accessToken, 'skin_rifle_neon_01', 'IN_GAME', 'coin-buy-0002');
    expect(owned.body.error.code).toBe('ALREADY_OWNED');
    const reused = await order(g.accessToken, 'banner_irani_chai', 'IN_GAME', 'coin-buy-0001');
    expect(reused.body.error.code).toBe('IDEMPOTENCY_KEY_REUSED');
    const wrongPlatform = await order(
      g.accessToken,
      'skin_rifle_charminar_gold',
      'IN_GAME',
      'coin-buy-0003',
    );
    expect(wrongPlatform.status).toBe(422);
    const notConfigured = await order(
      g.accessToken,
      'skin_rifle_charminar_gold',
      'ANDROID',
      'coin-buy-0004',
    );
    expect(notConfigured.status).toBe(503);
    expect(notConfigured.body.error.code).toBe('PLATFORM_UNAVAILABLE');

    const list = await api()
      .get('/api/v1/store/orders')
      .set(...bearer(g.accessToken));
    expectContract(list, 'GET', '/store/orders');
    expect(list.body.items).toHaveLength(1);
  });

  it('verifies a real-money purchase once and rejects forged or reused receipts', async () => {
    const g = await guest(h);
    const o = await order(g.accessToken, 'skin_rifle_charminar_gold', 'DEV', 'inr-buy-0001');
    expect(o.status).toBe(201);
    expect(o.body).toMatchObject({ status: 'PENDING', sku: 'skin_rifle_charminar_gold' });

    const forged = await verify(
      g.accessToken,
      o.body.orderId,
      devReceipt('x'.repeat(40), o.body.orderId, 'skin_rifle_charminar_gold', 'tx-a'),
    );
    expect(forged.status).toBe(422);
    expect(forged.body.error.code).toBe('RECEIPT_REJECTED');
    expectContract(forged, 'POST', '/store/orders/{orderId}/verify');
    const wrongSku = await verify(
      g.accessToken,
      o.body.orderId,
      devReceipt(DEV_PURCHASE_SECRET, o.body.orderId, 'gems_100', 'tx-a'),
    );
    expect(wrongSku.status).toBe(422);

    const receipt = devReceipt(
      DEV_PURCHASE_SECRET,
      o.body.orderId,
      'skin_rifle_charminar_gold',
      'tx-a',
    );
    const ok = await verify(g.accessToken, o.body.orderId, receipt);
    expect(ok.status).toBe(200);
    expectContract(ok, 'POST', '/store/orders/{orderId}/verify');
    expect(ok.body.status).toBe('FULFILLED');
    const retry = await verify(g.accessToken, o.body.orderId, receipt);
    expect(retry.status).toBe(200);
    expect(retry.body.orderId).toBe(o.body.orderId);

    // The same platform transaction cannot pay for a second order, for anyone.
    const other = await guest(h);
    const o2 = await order(other.accessToken, 'skin_rifle_charminar_gold', 'DEV', 'inr-buy-0002');
    const reuse = await verify(
      other.accessToken,
      o2.body.orderId,
      devReceipt(DEV_PURCHASE_SECRET, o2.body.orderId, 'skin_rifle_charminar_gold', 'tx-a'),
    );
    expect(reuse.status).toBe(409);
    expect(reuse.body.error.code).toBe('RECEIPT_ALREADY_USED');
    const notMine = await verify(other.accessToken, o.body.orderId, receipt);
    expect(notMine.status).toBe(404);

    const owned = await order(g.accessToken, 'skin_rifle_charminar_gold', 'DEV', 'inr-buy-0003');
    expect(owned.body.error.code).toBe('ALREADY_OWNED');
  });

  it('refunds from a platform notification exactly once, recording any shortfall', async () => {
    const g = await guest(h);
    const orderId = await devBuy(g.accessToken, 'gems_100', 'tx-gems-1');
    expect((await inventory(g.accessToken)).wallets.GEM).toBe(100);
    // The player spends 60 gems before the refund lands.
    await h.app
      .get(Db)
      .tx((c) => debit(c, g.userId, 'GEM', 60, 'ADMIN_ADJUSTMENT', 'test', `spend-${g.userId}`));

    const bad = await devHook(
      { eventId: 'evt-1', type: 'REFUND', transactionId: 'tx-gems-1' },
      'y'.repeat(40),
    );
    expect(bad.status).toBe(401);
    const first = await devHook({ eventId: 'evt-1', type: 'REFUND', transactionId: 'tx-gems-1' });
    expect(first.status).toBe(200);
    expectContract(first, 'POST', '/webhooks/dev');
    expect(first.body.outcome).toBe('APPLIED');
    const dup = await devHook({ eventId: 'evt-1', type: 'REFUND', transactionId: 'tx-gems-1' });
    expect(dup.body.outcome).toBe('APPLIED');
    const second = await devHook({ eventId: 'evt-2', type: 'REFUND', transactionId: 'tx-gems-1' });
    expect(second.body.outcome).toBe('APPLIED');

    expect((await inventory(g.accessToken)).wallets.GEM).toBe(0);
    const o = await h.app
      .get(Db)
      .one<{ status: string; refund_shortfall: string }>(
        'SELECT status, refund_shortfall FROM orders WHERE id = $1',
        [orderId],
      );
    expect(o).toEqual({ status: 'REFUNDED', refund_shortfall: '60' });
    const drift = await h.app
      .get(Db)
      .one<{ n: number }>('SELECT count(*)::int AS n FROM wallet_ledger_drift');
    expect(drift!.n).toBe(0);

    const unknown = await devHook({ eventId: 'evt-3', type: 'REFUND', transactionId: 'tx-nobody' });
    expect(unknown.body.outcome).toBe('UNMATCHED');
  });

  it('revokes a refunded cosmetic and puts the starter item back in its slot', async () => {
    const g = await guest(h);
    await devBuy(g.accessToken, 'skin_rifle_charminar_gold', 'tx-gold-2');
    const equip = await api()
      .put('/api/v1/inventory/loadout/WEAPON_SKIN')
      .set(...bearer(g.accessToken))
      .send({ cosmeticKey: 'skin_rifle_charminar_gold' });
    expect(equip.body.loadout.WEAPON_SKIN).toBe('skin_rifle_charminar_gold');

    expect(
      (await devHook({ eventId: 'evt-gold-2', type: 'REFUND', transactionId: 'tx-gold-2' })).body
        .outcome,
    ).toBe('APPLIED');
    const inv = await inventory(g.accessToken);
    expect(inv.items.map((i: { cosmeticKey: string }) => i.cosmeticKey)).not.toContain(
      'skin_rifle_charminar_gold',
    );
    expect(inv.loadout.WEAPON_SKIN).toBe('skin_rifle_default');
  });

  it('restores an unconfirmed purchase for its owner only', async () => {
    const g = await guest(h);
    const restore = (token: string, tx: string) =>
      api()
        .post('/api/v1/store/restore')
        .set(...bearer(token))
        .send({
          platform: 'DEV',
          sku: 'outfit_lakeside_runner',
          proof: devReceipt(DEV_PURCHASE_SECRET, 'none', 'outfit_lakeside_runner', tx),
        });

    const first = await restore(g.accessToken, 'tx-restore-1');
    expect(first.status).toBe(200);
    expectContract(first, 'POST', '/store/restore');
    expect(first.body.status).toBe('FULFILLED');
    const again = await restore(g.accessToken, 'tx-restore-1');
    expect(again.body.orderId).toBe(first.body.orderId);
    const thief = await guest(h);
    const stolen = await restore(thief.accessToken, 'tx-restore-1');
    expect(stolen.status).toBe(409);
    expect(stolen.body.error.code).toBe('RECEIPT_ALREADY_USED');
  });

  it('honours the store and matchmaking kill switches', async () => {
    const g = await guest(h);
    const db = h.app.get(Db);
    const cfg = h.app.get(GameConfigService);
    await db.query(
      `INSERT INTO game_config_versions (version, config, reason, created_by)
       SELECT max(version) + 1, '{"storeEnabled": false, "matchmakingEnabled": false, "disabledProducts": [], "disabledWeapons": []}', 'test', 'test'
         FROM game_config_versions`,
    );
    cfg.invalidate();
    try {
      const cat = await api()
        .get('/api/v1/store/catalog')
        .set(...bearer(g.accessToken));
      expect(cat.body).toEqual({ storeEnabled: false, items: [] });
      const buy = await order(g.accessToken, 'banner_irani_chai', 'IN_GAME', 'kill-0001');
      expect(buy.status).toBe(503);
      expect(buy.body.error.code).toBe('STORE_DISABLED');
      const q = await api()
        .post('/api/v1/matchmaking/queue')
        .set(...bearer(g.accessToken))
        .send({ mode: 'SOLO', region: 'IN-SOUTH', clientVersion: '0.5.0' });
      expect(q.status).toBe(503);
      const pub = await api().get('/api/v1/config');
      expectContract(pub, 'GET', '/config');
      expect(pub.body).toMatchObject({ storeEnabled: false, matchmakingEnabled: false });
    } finally {
      await db.query(
        `INSERT INTO game_config_versions (version, config, reason, created_by)
         SELECT max(version) + 1, '{"storeEnabled": true, "matchmakingEnabled": true, "disabledProducts": ["banner_irani_chai"], "disabledWeapons": []}', 'test', 'test'
           FROM game_config_versions`,
      );
      cfg.invalidate();
    }
    const disabled = await order(g.accessToken, 'banner_irani_chai', 'IN_GAME', 'kill-0002');
    expect(disabled.body.error.code).toBe('PRODUCT_UNAVAILABLE');
  });

  it('deletes an account after the grace period, keeping financial records', async () => {
    const g = await guest(h);
    const orderId = await devBuy(g.accessToken, 'outfit_lakeside_runner', 'tx-deleted-user');

    const req = await api()
      .post('/api/v1/player/deletion')
      .set(...bearer(g.accessToken));
    expect(req.status).toBe(202);
    expectContract(req, 'POST', '/player/deletion');
    expect(req.body.status).toBe('PENDING_DELETION');
    // Signing in during the grace period works, so the player can cancel.
    const signIn = await api().post('/api/v1/auth/guest').send(g.guest);
    expect(signIn.status).toBe(200);
    const cancel = await api()
      .delete('/api/v1/player/deletion')
      .set(...bearer(g.accessToken));
    expect(cancel.body).toEqual({ status: 'ACTIVE', scheduledFor: null });
    expectContract(cancel, 'DELETE', '/player/deletion');

    await api()
      .post('/api/v1/player/deletion')
      .set(...bearer(g.accessToken));
    await h.app
      .get(Db)
      .query(
        `UPDATE account_deletion_requests SET requested_at = now() - interval '31 days', scheduled_for = now() - interval '1 day' WHERE user_id = $1`,
        [g.userId],
      );
    expect(await h.app.get(JobsService).processDeletions()).toBeGreaterThanOrEqual(1);

    const after = await api().post('/api/v1/auth/guest').send(g.guest);
    expect(after.status).toBe(401);
    const pub = await api().get(`/api/v1/players/${g.userId}/public`);
    expect(pub.status).toBe(404);
    const kept = await h.app
      .get(Db)
      .one<{ status: string }>('SELECT status FROM orders WHERE id = $1', [orderId]);
    expect(kept!.status).toBe('FULFILLED');
  });

  it('serves public profiles and events', async () => {
    const g = await guest(h, 'Public_Pat');
    const pub = await api().get(`/api/v1/players/${g.userId}/public`);
    expect(pub.status).toBe(200);
    expectContract(pub, 'GET', '/players/{playerId}/public');
    expect(pub.body).toMatchObject({ displayName: 'Public_Pat', level: 1, wins: 0 });
    const ev = await api().get('/api/v1/events');
    expectContract(ev, 'GET', '/events');
  });
});
