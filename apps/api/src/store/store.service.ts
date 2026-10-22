import { Inject, Injectable, Logger } from '@nestjs/common';
import { PoolClient } from 'pg';
import { ApiError, Conflict, Invalid, NotFound } from '../common/api-error';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';
import { credit } from '../matchmaking/results.service';
import { GameConfigService } from './game-config.service';
import { debit, lockWallet } from './ledger';
import {
  AppleVerifier,
  DevVerifier,
  GoogleVerifier,
  PurchaseVerifier,
  StorePlatform,
  VerifiedPurchase,
} from './verifiers';

export type OrderPlatform = StorePlatform | 'IN_GAME';
type Currency = 'INR' | 'COIN' | 'GEM';

interface ProductRow {
  id: string;
  key: string;
  display_name: string;
  description: string;
  price_currency: Currency;
  price_minor: string;
}

interface GrantRow {
  id: string;
  grant_type: 'COSMETIC' | 'CURRENCY' | 'SEASON_PASS';
  cosmetic_id: string | null;
  season_id: string | null;
  season_key: string | null;
  cosmetic_key: string | null;
  cosmetic_kind: string | null;
  currency_code: 'COIN' | 'GEM' | null;
  amount: string | null;
}

export interface OrderRow {
  id: string;
  user_id: string;
  product_id: string;
  product_key: string;
  platform: OrderPlatform;
  status: string;
  amount_minor: string;
  currency_code: Currency;
  idempotency_key: string;
  failure_reason: string | null;
  refund_shortfall: string;
  created_at: Date;
  fulfilled_at: Date | null;
  refunded_at: Date | null;
}

const ORDER_SELECT = `SELECT o.id, o.user_id, o.product_id, p.key AS product_key, o.platform, o.status, o.amount_minor,
       o.currency_code, o.idempotency_key, o.failure_reason, o.refund_shortfall, o.created_at, o.fulfilled_at, o.refunded_at
  FROM orders o JOIN store_products p ON p.id = o.product_id`;

/** Products a player can buy right now. */
const AVAILABLE = `p.status = 'PUBLISHED' AND (p.available_from IS NULL OR p.available_from <= now())
  AND (p.available_until IS NULL OR p.available_until > now())`;

export function orderView(o: OrderRow, sku?: string | null) {
  return {
    orderId: o.id,
    productKey: o.product_key,
    platform: o.platform,
    status: o.status,
    price: { currency: o.currency_code, amountMinor: Number(o.amount_minor) },
    sku: sku ?? null,
    createdAt: o.created_at.toISOString(),
    fulfilledAt: o.fulfilled_at?.toISOString() ?? null,
    refundedAt: o.refunded_at?.toISOString() ?? null,
  };
}

const isUniqueViolation = (err: unknown) => (err as { code?: string })?.code === '23505';

/**
 * The store (BRD §9 "Store", "Payments"). Prices come from the server only; clients send a product key.
 *  - Coin/gem products: one transaction debits the ledger and grants the item, keyed by the client's idempotency key.
 *  - Real-money products: an order is created first, the platform purchase is verified server-to-server, then one
 *    transaction records the payment (unique per platform transaction) and grants. A receipt can fulfil one order once.
 *  - Refunds revoke what the order granted and claw back currency (any shortfall is recorded, never negative).
 */
@Injectable()
export class StoreService {
  private readonly log = new Logger('Store');
  readonly verifiers = new Map<StorePlatform, PurchaseVerifier>();

  constructor(
    private readonly db: Db,
    private readonly gameConfig: GameConfigService,
    @Inject(APP_CONFIG) config: AppConfig,
  ) {
    if (config.DEV_PURCHASES_ENABLED && config.DEV_PURCHASE_SECRET)
      this.verifiers.set('DEV', new DevVerifier(config.DEV_PURCHASE_SECRET));
    if (config.GOOGLE_PLAY_PACKAGE_NAME && config.GOOGLE_SERVICE_ACCOUNT_JSON) {
      const account = JSON.parse(config.GOOGLE_SERVICE_ACCOUNT_JSON) as {
        client_email: string;
        private_key: string;
      };
      this.verifiers.set(
        'ANDROID',
        new GoogleVerifier(config.GOOGLE_PLAY_PACKAGE_NAME, account, {
          api: config.GOOGLE_API_BASE_URL,
          token: config.GOOGLE_TOKEN_URL,
        }),
      );
    }
    if (config.APPLE_BUNDLE_ID && config.APPLE_ROOT_CERT_PEM)
      this.verifiers.set(
        'IOS',
        new AppleVerifier(config.APPLE_BUNDLE_ID, config.APPLE_ROOT_CERT_PEM),
      );
  }

  // ---- Catalogue ----

  async catalog(userId: string) {
    const { config } = await this.gameConfig.current();
    if (!config.storeEnabled) return { storeEnabled: false, items: [] };
    const products = await this.db.query<ProductRow & { owned: boolean }>(
      `SELECT p.id, p.key, p.display_name, p.description, p.price_currency, p.price_minor,
              (EXISTS (SELECT 1 FROM store_product_grants g WHERE g.product_id = p.id AND g.grant_type <> 'CURRENCY')
               AND NOT EXISTS (SELECT 1 FROM store_product_grants g
                                WHERE g.product_id = p.id AND (g.grant_type = 'CURRENCY'
                                  OR (g.grant_type = 'COSMETIC' AND NOT EXISTS (
                                    SELECT 1 FROM player_inventory i WHERE i.user_id = $1 AND i.cosmetic_id = g.cosmetic_id))
                                  OR (g.grant_type = 'SEASON_PASS' AND NOT EXISTS (
                                    SELECT 1 FROM season_progress sp WHERE sp.user_id = $1 AND sp.season_id = g.season_id AND sp.premium)))))
                AS owned
         FROM store_products p
        WHERE ${AVAILABLE} AND NOT (p.key = ANY($2))
        ORDER BY p.price_currency, p.price_minor, p.key`,
      [userId, config.disabledProducts],
    );
    const ids = products.map((p) => p.id);
    const grants = await this.db.query<GrantRow & { product_id: string }>(
      `SELECT g.product_id, g.id, g.grant_type, g.cosmetic_id, c.key AS cosmetic_key, c.kind AS cosmetic_kind,
              g.currency_code, g.amount, g.season_id, se.key AS season_key
         FROM store_product_grants g LEFT JOIN cosmetics c ON c.id = g.cosmetic_id LEFT JOIN seasons se ON se.id = g.season_id
        WHERE g.product_id = ANY($1) ORDER BY g.grant_type, c.key`,
      [ids],
    );
    const skus = await this.db.query<{ product_id: string; platform: string; sku: string }>(
      'SELECT product_id, platform, sku FROM store_product_platform_skus WHERE product_id = ANY($1)',
      [ids],
    );
    return {
      storeEnabled: true,
      items: products.map((p) => ({
        productKey: p.key,
        displayName: p.display_name,
        description: p.description,
        price: { currency: p.price_currency, amountMinor: Number(p.price_minor) },
        grants: grants.filter((g) => g.product_id === p.id).map(grantView),
        skus: Object.fromEntries(
          skus.filter((s) => s.product_id === p.id).map((s) => [s.platform, s.sku]),
        ),
        owned: p.owned,
      })),
    };
  }

  // ---- Orders ----

  async createOrder(
    userId: string,
    req: { productKey: string; platform: OrderPlatform; idempotencyKey: string },
  ) {
    await this.gameConfig.assertStoreOpen(req.productKey);
    const existing = await this.findByIdempotencyKey(userId, req.idempotencyKey);
    if (existing) return this.replay(existing, req);

    const product = await this.db.one<ProductRow>(
      `SELECT p.id, p.key, p.display_name, p.description, p.price_currency, p.price_minor
         FROM store_products p WHERE p.key = $1 AND ${AVAILABLE}`,
      [req.productKey],
    );
    if (!product) throw NotFound('Product');
    const grants = await this.grantsOf(product.id);

    if (product.price_currency === 'INR') {
      if (req.platform === 'IN_GAME')
        throw Invalid('This product is bought through the app store', [
          { path: 'platform', message: 'use ANDROID or IOS' },
        ]);
      const sku = await this.skuFor(product, req.platform);
      if (!this.verifiers.has(req.platform))
        throw new ApiError(
          503,
          'PLATFORM_UNAVAILABLE',
          `${req.platform} purchases are not available`,
        );
      if (await this.ownsAll(this.db, userId, grants))
        throw Conflict('ALREADY_OWNED', 'You already own this item');
      const order = await this.insertOrder(this.db, userId, product, req);
      return orderView(order, sku);
    }

    if (req.platform !== 'IN_GAME')
      throw Invalid('Coin and gem products are bought in game', [
        { path: 'platform', message: 'use IN_GAME' },
      ]);
    const currency = product.price_currency;
    const order = await this.db.tx(async (c) => {
      // The wallet lock serialises this player's purchases in this currency, so the ownership check holds.
      await lockWallet(c, userId, currency);
      if (await this.ownsAll(c, userId, grants))
        throw Conflict('ALREADY_OWNED', 'You already own this item');
      const o = await this.insertOrder(c, userId, product, req);
      const price = Number(product.price_minor);
      const taken = await debit(
        c,
        userId,
        currency,
        price,
        'PURCHASE',
        `order:${o.id}`,
        `order:${o.id}:pay`,
      );
      if (taken !== price) throw Conflict('INSUFFICIENT_FUNDS', `Not enough ${currency}`);
      await this.grant(c, o, grants);
      return this.markFulfilled(c, o.id);
    });
    return orderView(order);
  }

  async listOrders(userId: string) {
    const rows = await this.db.query<OrderRow>(
      `${ORDER_SELECT} WHERE o.user_id = $1 ORDER BY o.created_at DESC LIMIT 100`,
      [userId],
    );
    return { items: rows.map((o) => orderView(o)) };
  }

  async getOrder(userId: string, orderId: string) {
    const o = await this.db.one<OrderRow>(`${ORDER_SELECT} WHERE o.id = $1 AND o.user_id = $2`, [
      orderId,
      userId,
    ]);
    if (!o) throw NotFound('Order');
    return orderView(o, await this.skuForOrder(o));
  }

  /** Verifies the platform purchase for a pending order and fulfils it exactly once. Safe to retry. */
  async verify(userId: string, orderId: string, proof: string) {
    const o = await this.db.one<OrderRow>(`${ORDER_SELECT} WHERE o.id = $1 AND o.user_id = $2`, [
      orderId,
      userId,
    ]);
    if (!o) throw NotFound('Order');
    if (o.status === 'FULFILLED') return orderView(o, await this.skuForOrder(o));
    if (o.status !== 'PENDING') throw Conflict('ORDER_CLOSED', `Order is ${o.status}`);
    if (o.platform === 'IN_GAME') throw Invalid('In-game orders need no receipt');
    const verifier = this.verifierFor(o.platform);
    const sku = (await this.skuForOrder(o))!;
    const purchase = await verifier.verify(proof, sku, o.id);
    const fulfilled = await this.fulfil(o, purchase);
    await this.acknowledge(purchase, o.id);
    return orderView(fulfilled, sku);
  }

  /**
   * Restores a platform purchase the player made but the game never confirmed (app killed after paying, reinstall).
   * Re-grants an already fulfilled order idempotently; creates and fulfils an order if none exists yet.
   */
  async restore(userId: string, platform: StorePlatform, sku: string, proof: string) {
    await this.gameConfig.assertStoreOpen();
    const verifier = this.verifierFor(platform);
    const product = await this.db.one<ProductRow>(
      platform === 'DEV'
        ? `SELECT p.id, p.key, p.display_name, p.description, p.price_currency, p.price_minor
             FROM store_products p WHERE p.key = $1 AND p.price_currency = 'INR' AND p.status IN ('PUBLISHED', 'RETIRED')`
        : `SELECT p.id, p.key, p.display_name, p.description, p.price_currency, p.price_minor
             FROM store_products p JOIN store_product_platform_skus s ON s.product_id = p.id
            WHERE s.platform = $2 AND s.sku = $1`,
      platform === 'DEV' ? [sku] : [sku, platform],
    );
    if (!product) throw NotFound('Product');
    const purchase = await verifier.verify(proof, sku);

    const paid = await this.db.one<OrderRow>(
      `${ORDER_SELECT} JOIN payment_transactions t ON t.order_id = o.id
        WHERE t.platform = $1 AND t.provider_transaction_id = $2`,
      [platform, purchase.providerTransactionId],
    );
    if (paid) {
      if (paid.user_id !== userId)
        throw Conflict('RECEIPT_ALREADY_USED', 'This purchase belongs to another account');
      if (paid.status === 'FULFILLED') {
        await this.db.tx((c) => this.grant(c, paid, this.grantsOf(paid.product_id, c)));
      }
      await this.acknowledge(purchase, paid.id);
      return orderView(paid, sku);
    }
    const order = await this.insertOrder(this.db, userId, product, {
      platform,
      idempotencyKey: `restore:${platform}:${purchase.providerTransactionId}`.slice(0, 100),
    });
    const fulfilled = await this.fulfil(order, purchase);
    await this.acknowledge(purchase, order.id);
    return orderView(fulfilled, sku);
  }

  // ---- Refunds (platform notifications and admins) ----

  /** Revokes what the order granted. Idempotent: refunding a refunded order is a no-op. */
  async refund(orderId: string, actor: string, reason: string, requestId?: string) {
    return this.db.tx(async (c) => {
      const { rows } = await c.query<OrderRow>(`${ORDER_SELECT} WHERE o.id = $1 FOR UPDATE OF o`, [
        orderId,
      ]);
      const o = rows[0];
      if (!o) throw NotFound('Order');
      if (o.status === 'REFUNDED')
        return {
          order: orderView(o),
          alreadyRefunded: true,
          shortfall: Number(o.refund_shortfall),
        };
      if (o.status !== 'FULFILLED') throw Conflict('ORDER_NOT_REFUNDABLE', `Order is ${o.status}`);

      let shortfall = 0;
      for (const g of await this.grantsOf(o.product_id, c)) {
        if (g.grant_type === 'COSMETIC') {
          await c.query(
            `DELETE FROM player_inventory WHERE user_id = $1 AND cosmetic_id = $2 AND source = 'PURCHASE' AND source_reference = $3`,
            [o.user_id, g.cosmetic_id, `order:${o.id}`],
          );
          // Put the starter item back in the slot the refunded one left empty.
          await c.query(
            `INSERT INTO loadout_slots (user_id, slot, cosmetic_id)
             SELECT i.user_id, c.kind, c.id FROM player_inventory i JOIN cosmetics c ON c.id = i.cosmetic_id
              WHERE i.user_id = $1 AND i.source = 'STARTER' AND c.kind = $2
              ORDER BY c.key LIMIT 1
             ON CONFLICT (user_id, slot) DO NOTHING`,
            [o.user_id, g.cosmetic_kind],
          );
        } else if (g.grant_type === 'SEASON_PASS') {
          shortfall += await this.revokeSeasonPass(c, o.user_id, g.season_id!, o.id);
        } else {
          const amount = Number(g.amount);
          const taken = await debit(
            c,
            o.user_id,
            g.currency_code!,
            amount,
            'REFUND',
            `order:${o.id}`,
            `order:${o.id}:refund:${g.id}`,
            true,
          );
          shortfall += amount - taken;
        }
      }
      if (o.platform === 'IN_GAME') {
        await credit(
          c,
          o.user_id,
          o.currency_code as 'COIN' | 'GEM',
          Number(o.amount_minor),
          'REFUND',
          `order:${o.id}`,
          `order:${o.id}:refund`,
        );
      }
      await c.query(
        `UPDATE orders SET status = 'REFUNDED', refunded_at = now(), refund_shortfall = $2 WHERE id = $1`,
        [o.id, shortfall],
      );
      await c.query(
        `UPDATE payment_transactions SET status = 'REFUNDED', refunded_at = now() WHERE order_id = $1`,
        [o.id],
      );
      await c.query(
        `INSERT INTO admin_audit_logs (actor, action, entity_type, entity_id, reason, details, request_id)
         VALUES ($1, 'order.refund', 'order', $2, $3, $4, $5)`,
        [
          actor,
          o.id,
          reason,
          { shortfall, platform: o.platform, userId: o.user_id },
          requestId ?? null,
        ],
      );
      const { rows: after } = await c.query<OrderRow>(`${ORDER_SELECT} WHERE o.id = $1`, [o.id]);
      return { order: orderView(after[0]!), alreadyRefunded: false, shortfall };
    });
  }

  /** Finds the order a platform transaction paid for. */
  async orderForTransaction(
    platform: StorePlatform,
    providerTransactionId: string,
  ): Promise<string | undefined> {
    const row = await this.db.one<{ order_id: string }>(
      'SELECT order_id FROM payment_transactions WHERE platform = $1 AND provider_transaction_id = $2',
      [platform, providerTransactionId],
    );
    return row?.order_id;
  }

  /** Cancels abandoned pending orders. Returns how many were cancelled. */
  async cancelStalePending(olderThanHours: number): Promise<number> {
    const rows = await this.db.query(
      `UPDATE orders SET status = 'CANCELLED', failure_reason = 'not completed in time'
        WHERE status = 'PENDING' AND created_at < now() - make_interval(hours => $1::int) RETURNING id`,
      [olderThanHours],
    );
    return rows.length;
  }

  // ---- Internals ----

  verifierFor(platform: OrderPlatform): PurchaseVerifier {
    const v = platform === 'IN_GAME' ? undefined : this.verifiers.get(platform);
    if (!v)
      throw new ApiError(503, 'PLATFORM_UNAVAILABLE', `${platform} purchases are not available`);
    return v;
  }

  private async fulfil(o: OrderRow, purchase: VerifiedPurchase): Promise<OrderRow> {
    try {
      return await this.db.tx(async (c) => {
        const { rows } = await c.query<OrderRow>(
          `${ORDER_SELECT} WHERE o.id = $1 FOR UPDATE OF o`,
          [o.id],
        );
        const locked = rows[0]!;
        if (locked.status === 'FULFILLED') {
          const t = await c.query(
            'SELECT 1 FROM payment_transactions WHERE order_id = $1 AND provider_transaction_id = $2',
            [o.id, purchase.providerTransactionId],
          );
          if (t.rowCount) return locked; // a concurrent retry of the same receipt won
          throw Conflict('ORDER_CLOSED', 'Order was fulfilled with a different purchase');
        }
        if (locked.status !== 'PENDING')
          throw Conflict('ORDER_CLOSED', `Order is ${locked.status}`);
        await c.query(
          `INSERT INTO payment_transactions (order_id, platform, provider_transaction_id, purchase_token_hash, environment)
           VALUES ($1, $2, $3, $4, $5)`,
          [
            o.id,
            o.platform,
            purchase.providerTransactionId,
            purchase.tokenHash,
            purchase.environment,
          ],
        );
        await this.grant(c, locked, this.grantsOf(locked.product_id, c));
        return this.markFulfilled(c, o.id);
      });
    } catch (err) {
      if (isUniqueViolation(err))
        throw Conflict('RECEIPT_ALREADY_USED', 'This purchase was already used for another order');
      throw err;
    }
  }

  private async acknowledge(purchase: VerifiedPurchase, orderId: string): Promise<void> {
    if (!purchase.acknowledge) return;
    try {
      await purchase.acknowledge();
    } catch (err) {
      // Not fatal: the grant is committed. Google refunds unacknowledged purchases after 3 days, which then arrives
      // as a voided-purchase notification and revokes the grant, so the books still balance.
      this.log.warn(`acknowledge failed for order ${orderId}: ${(err as Error).message}`);
    }
  }

  private async grant(
    c: PoolClient,
    o: { id: string; user_id: string },
    grantsP: Promise<GrantRow[]> | GrantRow[],
  ) {
    for (const g of await grantsP) {
      if (g.grant_type === 'COSMETIC') {
        await c.query(
          `INSERT INTO player_inventory (user_id, cosmetic_id, source, source_reference)
           VALUES ($1, $2, 'PURCHASE', $3) ON CONFLICT (user_id, cosmetic_id) DO NOTHING`,
          [o.user_id, g.cosmetic_id, `order:${o.id}`],
        );
      } else if (g.grant_type === 'SEASON_PASS') {
        await c.query(
          `INSERT INTO season_progress (season_id, user_id, premium, premium_order_id) VALUES ($1, $2, true, $3)
           ON CONFLICT (season_id, user_id) DO UPDATE
             SET premium = true, premium_order_id = COALESCE(season_progress.premium_order_id, EXCLUDED.premium_order_id)`,
          [g.season_id, o.user_id, o.id],
        );
      } else {
        await credit(
          c,
          o.user_id,
          g.currency_code!,
          Number(g.amount),
          'PURCHASE',
          `order:${o.id}`,
          `order:${o.id}:grant:${g.id}`,
        );
      }
    }
  }

  private async markFulfilled(c: PoolClient, orderId: string): Promise<OrderRow> {
    await c.query(`UPDATE orders SET status = 'FULFILLED', fulfilled_at = now() WHERE id = $1`, [
      orderId,
    ]);
    const { rows } = await c.query<OrderRow>(`${ORDER_SELECT} WHERE o.id = $1`, [orderId]);
    return rows[0]!;
  }

  private async insertOrder(
    q: Db | PoolClient,
    userId: string,
    product: ProductRow,
    req: { platform: OrderPlatform; idempotencyKey: string },
  ): Promise<OrderRow> {
    const sql = `WITH ins AS (
        INSERT INTO orders (user_id, product_id, platform, amount_minor, currency_code, idempotency_key)
        VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (user_id, idempotency_key) DO NOTHING RETURNING id)
      SELECT id FROM ins`;
    const params = [
      userId,
      product.id,
      req.platform,
      product.price_minor,
      product.price_currency,
      req.idempotencyKey,
    ];
    const inserted =
      q instanceof Db
        ? await q.one<{ id: string }>(sql, params)
        : (await q.query<{ id: string }>(sql, params)).rows[0];
    if (!inserted) {
      // A concurrent request with the same key won the race.
      const other = await this.findByIdempotencyKey(userId, req.idempotencyKey);
      throw Conflict(
        'ORDER_IN_PROGRESS',
        `An order with this idempotency key already exists (${other?.status ?? 'unknown'})`,
      );
    }
    const select = `${ORDER_SELECT} WHERE o.id = $1`;
    return q instanceof Db
      ? (await q.one<OrderRow>(select, [inserted.id]))!
      : (await q.query<OrderRow>(select, [inserted.id])).rows[0]!;
  }

  private async findByIdempotencyKey(userId: string, key: string) {
    return this.db.one<OrderRow>(
      `${ORDER_SELECT} WHERE o.user_id = $1 AND o.idempotency_key = $2`,
      [userId, key],
    );
  }

  private async replay(o: OrderRow, req: { productKey: string; platform: OrderPlatform }) {
    if (o.product_key !== req.productKey || o.platform !== req.platform)
      throw Conflict(
        'IDEMPOTENCY_KEY_REUSED',
        'This idempotency key was used for a different order',
      );
    return orderView(o, await this.skuForOrder(o));
  }

  private grantsOf(productId: string, q: Db | PoolClient = this.db): Promise<GrantRow[]> {
    const sql = `SELECT g.id, g.grant_type, g.cosmetic_id, c.key AS cosmetic_key, c.kind AS cosmetic_kind, g.currency_code, g.amount, g.season_id, se.key AS season_key
                   FROM store_product_grants g LEFT JOIN cosmetics c ON c.id = g.cosmetic_id LEFT JOIN seasons se ON se.id = g.season_id WHERE g.product_id = $1`;
    return q instanceof Db
      ? q.query<GrantRow>(sql, [productId])
      : q.query<GrantRow>(sql, [productId]).then((r) => r.rows);
  }

  /** True when the player already has everything the product grants (a currency pack can always be bought again). */
  private async ownsAll(q: Db | PoolClient, userId: string, all: GrantRow[]): Promise<boolean> {
    let grants = all;
    if (grants.length === 0 || grants.some((g) => g.grant_type === 'CURRENCY')) return false;
    for (const pass of grants.filter((g) => g.grant_type === 'SEASON_PASS')) {
      const sql = 'SELECT premium FROM season_progress WHERE user_id = $1 AND season_id = $2';
      const row =
        q instanceof Db
          ? await q.one<{ premium: boolean }>(sql, [userId, pass.season_id])
          : (await q.query<{ premium: boolean }>(sql, [userId, pass.season_id])).rows[0];
      if (!row?.premium) return false;
    }
    grants = grants.filter((g) => g.grant_type === 'COSMETIC');
    if (grants.length === 0) return true;
    const sql =
      'SELECT count(*)::int AS n FROM player_inventory WHERE user_id = $1 AND cosmetic_id = ANY($2)';
    const params = [userId, grants.map((g) => g.cosmetic_id)];
    const n =
      q instanceof Db
        ? (await q.one<{ n: number }>(sql, params))!.n
        : (await q.query<{ n: number }>(sql, params)).rows[0]!.n;
    return n === grants.length;
  }

  /**
   * Refunding a season pass removes premium access and what was claimed with it: premium cosmetics are revoked and
   * premium currency is clawed back (any amount already spent is returned as shortfall). Claims stay recorded.
   */
  private async revokeSeasonPass(
    c: PoolClient,
    userId: string,
    seasonId: string,
    orderId: string,
  ): Promise<number> {
    await c.query(
      `UPDATE season_progress SET premium = false WHERE season_id = $1 AND user_id = $2 AND premium_order_id = $3`,
      [seasonId, userId, orderId],
    );
    await c.query(
      `DELETE FROM player_inventory WHERE user_id = $1 AND source = 'SEASON_REWARD' AND source_reference LIKE $2`,
      [userId, `season:${seasonId}:%:PREMIUM`],
    );
    const { rows } = await c.query<{ tier: number; currency_code: 'COIN' | 'GEM'; amount: string }>(
      `SELECT cl.tier, r.currency_code, r.amount FROM season_claims cl
         JOIN season_rewards r ON r.season_id = cl.season_id AND r.tier = cl.tier AND r.track = cl.track
        WHERE cl.season_id = $1 AND cl.user_id = $2 AND cl.track = 'PREMIUM' AND r.currency_code IS NOT NULL`,
      [seasonId, userId],
    );
    let shortfall = 0;
    for (const r of rows) {
      const ref = `season:${seasonId}:${r.tier}:PREMIUM`;
      const amount = Number(r.amount);
      shortfall +=
        amount -
        (await debit(c, userId, r.currency_code, amount, 'REFUND', ref, `${ref}:refund`, true));
    }
    return shortfall;
  }

  private async skuFor(
    product: { id: string; key: string },
    platform: OrderPlatform,
  ): Promise<string> {
    if (platform === 'DEV') return product.key;
    const row = await this.db.one<{ sku: string }>(
      'SELECT sku FROM store_product_platform_skus WHERE product_id = $1 AND platform = $2',
      [product.id, platform],
    );
    if (!row)
      throw new ApiError(409, 'PRODUCT_UNAVAILABLE', `This product is not sold on ${platform}`);
    return row.sku;
  }

  private async skuForOrder(o: OrderRow): Promise<string | null> {
    if (o.platform === 'IN_GAME') return null;
    return this.skuFor({ id: o.product_id, key: o.product_key }, o.platform);
  }
}

function grantView(g: GrantRow) {
  if (g.grant_type === 'COSMETIC')
    return { type: 'COSMETIC' as const, cosmeticKey: g.cosmetic_key!, kind: g.cosmetic_kind! };
  if (g.grant_type === 'SEASON_PASS')
    return { type: 'SEASON_PASS' as const, seasonKey: g.season_key! };
  return { type: 'CURRENCY' as const, currency: g.currency_code!, amount: Number(g.amount) };
}
