import {
  Body,
  Controller,
  Get,
  HttpCode,
  Param,
  Patch,
  Post,
  Query,
  UseGuards,
} from '@nestjs/common';
import { PoolClient } from 'pg';
import { z } from 'zod';
import { Conflict, Forbidden, Invalid, NotFound, parse } from '../common/api-error';
import { parseUuid, uuid } from '../common/schemas';
import { Db } from '../database/database.module';
import { JobsService } from '../store/jobs.service';
import { OrderRow, orderView, StoreService } from '../store/store.service';
import {
  AdminAuthService,
  AdminGuard,
  AdminPrincipal,
  CurrentAdmin,
  RequestId,
  RequirePermission,
} from './admin-auth';

const productKey = z.string().regex(/^[a-z0-9_]{3,60}$/);
const sku = z.string().regex(/^[a-z0-9_.]{3,100}$/);
const grant = z.discriminatedUnion('type', [
  z.object({ type: z.literal('COSMETIC'), cosmeticKey: productKey }).strict(),
  z
    .object({
      type: z.literal('CURRENCY'),
      currency: z.enum(['COIN', 'GEM']),
      amount: z.number().int().min(1).max(1_000_000),
    })
    .strict(),
]);
const productFields = {
  displayName: z.string().min(1).max(60),
  description: z.string().max(300),
  price: z
    .object({
      currency: z.enum(['INR', 'COIN', 'GEM']),
      amountMinor: z.number().int().min(1).max(10_000_000),
    })
    .strict(),
  grants: z.array(grant).min(1).max(10),
  skus: z.object({ ANDROID: sku.optional(), IOS: sku.optional() }).strict(),
  availableFrom: z.string().datetime().nullable(),
  availableUntil: z.string().datetime().nullable(),
};
const createProduct = z
  .object({
    key: productKey,
    ...productFields,
    description: productFields.description.default(''),
    skus: productFields.skus.default({}),
  })
  .partial({ availableFrom: true, availableUntil: true })
  .strict();
const patchProduct = z.object(productFields).partial().strict();
const reasonBody = z.object({ reason: z.string().min(3).max(500) }).strict();

interface ProductRow {
  id: string;
  key: string;
  display_name: string;
  description: string;
  price_currency: string;
  price_minor: string;
  status: string;
  available_from: Date | null;
  available_until: Date | null;
  created_by: string;
  approved_by: string | null;
  published_at: Date | null;
  created_at: Date;
  updated_at: Date;
}

@Controller('admin')
@UseGuards(AdminGuard)
export class AdminEconomyController {
  constructor(
    private readonly db: Db,
    private readonly auth: AdminAuthService,
    private readonly store: StoreService,
    private readonly jobs: JobsService,
  ) {}

  // ---- Catalogue: DRAFT -> PENDING_APPROVAL -> PUBLISHED -> RETIRED, approved by a second person ----

  @Get('catalog/cosmetics')
  @RequirePermission('catalog.read')
  async cosmetics() {
    const rows = await this.db.query<{
      key: string;
      kind: string;
      display_name: string;
      rarity: string;
    }>('SELECT key, kind, display_name, rarity FROM cosmetics ORDER BY kind, key');
    return {
      items: rows.map((c) => ({
        cosmeticKey: c.key,
        kind: c.kind,
        displayName: c.display_name,
        rarity: c.rarity,
      })),
    };
  }

  @Get('catalog/products')
  @RequirePermission('catalog.read')
  async products(@Query() query: unknown) {
    const { status } = parse(
      z
        .object({
          status: z.enum(['DRAFT', 'PENDING_APPROVAL', 'PUBLISHED', 'RETIRED']).optional(),
        })
        .strict(),
      query,
    );
    const rows = await this.db.query<ProductRow>(
      'SELECT * FROM store_products WHERE ($1::text IS NULL OR status = $1) ORDER BY updated_at DESC LIMIT 200',
      [status ?? null],
    );
    const items = [];
    for (const r of rows) items.push(await this.view(this.db, r));
    return { items };
  }

  @Get('catalog/products/:key')
  @RequirePermission('catalog.read')
  async product(@Param('key') key: string) {
    return this.view(this.db, await this.load(this.db, key));
  }

  @Post('catalog/products')
  @HttpCode(201)
  @RequirePermission('catalog.write')
  async create(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Body() body: unknown,
  ) {
    const p = parse(createProduct, body);
    checkEconomy(p.price.currency, p.grants);
    try {
      return await this.db.tx(async (c) => {
        const { rows } = await c.query<ProductRow>(
          `INSERT INTO store_products (key, display_name, description, price_currency, price_minor, available_from, available_until, created_by)
           VALUES ($1, $2, $3, $4, $5, $6, $7, $8) RETURNING *`,
          [
            p.key,
            p.displayName,
            p.description,
            p.price.currency,
            p.price.amountMinor,
            p.availableFrom ?? null,
            p.availableUntil ?? null,
            admin.actor,
          ],
        );
        await this.replaceGrants(c, rows[0]!.id, p.grants);
        await this.replaceSkus(c, rows[0]!.id, p.skus);
        await this.auth.audit(
          c,
          admin.actor,
          'product.create',
          'product',
          p.key,
          null,
          { price: p.price },
          requestId,
        );
        return this.view(c, rows[0]!);
      });
    } catch (err) {
      if ((err as { code?: string }).code === '23505')
        throw Conflict('PRODUCT_EXISTS', 'Key or SKU is already used');
      throw err;
    }
  }

  @Patch('catalog/products/:key')
  @RequirePermission('catalog.write')
  async update(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('key') key: string,
    @Body() body: unknown,
  ) {
    const p = parse(patchProduct, body);
    try {
      return await this.db.tx(async (c) => {
        const cur = await this.load(c, key, true);
        if (cur.status !== 'DRAFT')
          throw Conflict(
            'PRODUCT_NOT_DRAFT',
            'Only drafts can be edited; published products are immutable',
          );
        const currency = p.price?.currency ?? cur.price_currency;
        checkEconomy(currency, p.grants ?? (await this.grantsOf(c, cur.id)));
        await c.query(
          `UPDATE store_products SET display_name = $2, description = $3, price_currency = $4, price_minor = $5,
             available_from = $6, available_until = $7 WHERE id = $1`,
          [
            cur.id,
            p.displayName ?? cur.display_name,
            p.description ?? cur.description,
            currency,
            p.price?.amountMinor ?? cur.price_minor,
            p.availableFrom !== undefined ? p.availableFrom : cur.available_from,
            p.availableUntil !== undefined ? p.availableUntil : cur.available_until,
          ],
        );
        if (p.grants) await this.replaceGrants(c, cur.id, p.grants);
        if (p.skus) await this.replaceSkus(c, cur.id, p.skus);
        await this.auth.audit(
          c,
          admin.actor,
          'product.update',
          'product',
          key,
          null,
          { fields: Object.keys(p) },
          requestId,
        );
        return this.view(c, await this.load(c, key));
      });
    } catch (err) {
      if ((err as { code?: string }).code === '23505')
        throw Conflict('PRODUCT_EXISTS', 'SKU is already used');
      throw err;
    }
  }

  @Post('catalog/products/:key/submit')
  @HttpCode(200)
  @RequirePermission('catalog.write')
  submit(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('key') key: string,
  ) {
    return this.transition(
      admin,
      requestId,
      key,
      'DRAFT',
      'PENDING_APPROVAL',
      'product.submit',
      null,
      async (c, cur) => {
        if (cur.price_currency === 'INR') {
          const { rowCount } = await c.query(
            'SELECT 1 FROM store_product_platform_skus WHERE product_id = $1',
            [cur.id],
          );
          if (!rowCount)
            throw Invalid('A real-money product needs an Android or iOS SKU', [
              { path: 'skus', message: 'required' },
            ]);
        }
      },
    );
  }

  @Post('catalog/products/:key/approve')
  @HttpCode(200)
  @RequirePermission('catalog.approve')
  approve(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('key') key: string,
  ) {
    return this.transition(
      admin,
      requestId,
      key,
      'PENDING_APPROVAL',
      'PUBLISHED',
      'product.approve',
      null,
      async (_c, cur) => {
        if (cur.created_by === admin.actor)
          throw Forbidden('SELF_APPROVAL', 'A second person must approve your product');
      },
    );
  }

  @Post('catalog/products/:key/reject')
  @HttpCode(200)
  @RequirePermission('catalog.approve')
  reject(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('key') key: string,
    @Body() body: unknown,
  ) {
    const { reason } = parse(reasonBody, body);
    return this.transition(
      admin,
      requestId,
      key,
      'PENDING_APPROVAL',
      'DRAFT',
      'product.reject',
      reason,
    );
  }

  @Post('catalog/products/:key/retire')
  @HttpCode(200)
  @RequirePermission('catalog.approve')
  retire(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('key') key: string,
    @Body() body: unknown,
  ) {
    const { reason } = parse(reasonBody, body);
    return this.transition(admin, requestId, key, 'PUBLISHED', 'RETIRED', 'product.retire', reason);
  }

  // ---- Orders ----

  @Get('orders')
  @RequirePermission('orders.read')
  async orders(@Query() query: unknown) {
    const q = parse(
      z
        .object({
          status: z
            .enum(['PENDING', 'VERIFIED', 'FULFILLED', 'FAILED', 'CANCELLED', 'REFUNDED'])
            .optional(),
          playerId: uuid.optional(),
          limit: z.coerce.number().int().min(1).max(100).default(50),
        })
        .strict(),
      query,
    );
    const rows = await this.db.query<OrderRow>(
      `SELECT o.*, p.key AS product_key FROM orders o JOIN store_products p ON p.id = o.product_id
        WHERE ($1::text IS NULL OR o.status = $1) AND ($2::uuid IS NULL OR o.user_id = $2)
        ORDER BY o.created_at DESC LIMIT $3`,
      [q.status ?? null, q.playerId ?? null, q.limit],
    );
    return { items: rows.map((o) => ({ ...orderView(o), playerId: o.user_id })) };
  }

  @Get('orders/:orderId')
  @RequirePermission('orders.read')
  async order(@Param('orderId') orderId: string) {
    const id = parseUuid(orderId, 'orderId');
    const o = await this.db.one<OrderRow>(
      'SELECT o.*, p.key AS product_key FROM orders o JOIN store_products p ON p.id = o.product_id WHERE o.id = $1',
      [id],
    );
    if (!o) throw NotFound('Order');
    const payment = await this.db.one<{
      platform: string;
      provider_transaction_id: string;
      environment: string;
      status: string;
      verified_at: Date;
    }>(
      'SELECT platform, provider_transaction_id, environment, status, verified_at FROM payment_transactions WHERE order_id = $1',
      [id],
    );
    const events = await this.db.query<{
      event_type: string;
      outcome: string;
      detail: string | null;
      received_at: Date;
    }>(
      'SELECT event_type, outcome, detail, received_at FROM purchase_events WHERE order_id = $1 ORDER BY id',
      [id],
    );
    return {
      ...orderView(o),
      playerId: o.user_id,
      failureReason: o.failure_reason,
      refundShortfall: Number(o.refund_shortfall),
      payment: payment
        ? {
            platform: payment.platform,
            providerTransactionId: payment.provider_transaction_id,
            environment: payment.environment,
            status: payment.status,
            verifiedAt: payment.verified_at.toISOString(),
          }
        : null,
      events: events.map((e) => ({
        type: e.event_type,
        outcome: e.outcome,
        detail: e.detail,
        receivedAt: e.received_at.toISOString(),
      })),
    };
  }

  /**
   * Refunds in the game: revokes grants (and returns coins for in-game purchases). Money for platform purchases is
   * returned in Google Play Console / App Store Connect; their refund notification then arrives as a no-op.
   */
  @Post('orders/:orderId/refund')
  @HttpCode(200)
  @RequirePermission('orders.refund')
  async refund(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('orderId') orderId: string,
    @Body() body: unknown,
  ) {
    const { reason } = parse(reasonBody, body);
    const r = await this.store.refund(
      parseUuid(orderId, 'orderId'),
      admin.actor,
      reason,
      requestId,
    );
    return { ...r.order, alreadyRefunded: r.alreadyRefunded, shortfall: r.shortfall };
  }

  @Post('reconcile')
  @HttpCode(200)
  @RequirePermission('orders.reconcile')
  async reconcile(@CurrentAdmin() admin: AdminPrincipal, @RequestId() requestId: string) {
    const report = await this.jobs.reconcile();
    await this.auth.audit(
      this.db,
      admin.actor,
      'jobs.reconcile',
      'system',
      'reconcile',
      null,
      { ...report },
      requestId,
    );
    return report;
  }

  // ---- Internals ----

  private async transition(
    admin: AdminPrincipal,
    requestId: string,
    key: string,
    from: string,
    to: string,
    action: string,
    reason: string | null,
    check?: (c: PoolClient, cur: ProductRow) => Promise<void>,
  ) {
    return this.db.tx(async (c) => {
      const cur = await this.load(c, key, true);
      if (cur.status !== from)
        throw Conflict('PRODUCT_STATE', `Product is ${cur.status}, expected ${from}`);
      await check?.(c, cur);
      if (to === 'PUBLISHED') {
        await c.query(
          `UPDATE store_products SET status = 'PUBLISHED', approved_by = $2, published_at = now() WHERE id = $1`,
          [cur.id, admin.actor],
        );
      } else {
        await c.query('UPDATE store_products SET status = $2 WHERE id = $1', [cur.id, to]);
      }
      await this.auth.audit(
        c,
        admin.actor,
        action,
        'product',
        key,
        reason,
        { from, to },
        requestId,
      );
      return this.view(c, await this.load(c, key));
    });
  }

  private async load(q: Db | PoolClient, key: string, lock = false): Promise<ProductRow> {
    const sql = `SELECT * FROM store_products WHERE key = $1${lock ? ' FOR UPDATE' : ''}`;
    const row =
      q instanceof Db
        ? await q.one<ProductRow>(sql, [key])
        : (await q.query<ProductRow>(sql, [key])).rows[0];
    if (!row) throw NotFound('Product');
    return row;
  }

  private async grantsOf(q: Db | PoolClient, productId: string) {
    const sql = `SELECT g.grant_type AS type, c.key AS "cosmeticKey", g.currency_code AS currency, g.amount
                   FROM store_product_grants g LEFT JOIN cosmetics c ON c.id = g.cosmetic_id WHERE g.product_id = $1 ORDER BY c.key`;
    const rows =
      q instanceof Db
        ? await q.query<{
            type: 'COSMETIC' | 'CURRENCY';
            cosmeticKey: string;
            currency: 'COIN' | 'GEM';
            amount: string;
          }>(sql, [productId])
        : (await q.query(sql, [productId])).rows;
    return rows.map((g) =>
      g.type === 'COSMETIC'
        ? { type: 'COSMETIC' as const, cosmeticKey: g.cosmeticKey as string }
        : {
            type: 'CURRENCY' as const,
            currency: g.currency as 'COIN' | 'GEM',
            amount: Number(g.amount),
          },
    );
  }

  private async view(q: Db | PoolClient, r: ProductRow) {
    const skuSql = 'SELECT platform, sku FROM store_product_platform_skus WHERE product_id = $1';
    const skus =
      q instanceof Db
        ? await q.query<{ platform: string; sku: string }>(skuSql, [r.id])
        : (await q.query(skuSql, [r.id])).rows;
    return {
      productKey: r.key,
      displayName: r.display_name,
      description: r.description,
      price: { currency: r.price_currency, amountMinor: Number(r.price_minor) },
      status: r.status,
      grants: await this.grantsOf(q, r.id),
      skus: Object.fromEntries(
        skus.map((s: { platform: string; sku: string }) => [s.platform, s.sku]),
      ),
      availableFrom: r.available_from?.toISOString() ?? null,
      availableUntil: r.available_until?.toISOString() ?? null,
      createdBy: r.created_by,
      approvedBy: r.approved_by,
      publishedAt: r.published_at?.toISOString() ?? null,
      updatedAt: r.updated_at.toISOString(),
    };
  }

  private async replaceGrants(c: PoolClient, productId: string, grants: z.infer<typeof grant>[]) {
    await c.query('DELETE FROM store_product_grants WHERE product_id = $1', [productId]);
    for (const g of grants) {
      if (g.type === 'COSMETIC') {
        const { rows } = await c.query<{ id: string }>('SELECT id FROM cosmetics WHERE key = $1', [
          g.cosmeticKey,
        ]);
        if (!rows[0])
          throw Invalid('Unknown cosmetic', [
            { path: 'grants', message: `${g.cosmeticKey} does not exist` },
          ]);
        await c.query(
          `INSERT INTO store_product_grants (product_id, grant_type, cosmetic_id) VALUES ($1, 'COSMETIC', $2)`,
          [productId, rows[0].id],
        );
      } else {
        await c.query(
          `INSERT INTO store_product_grants (product_id, grant_type, currency_code, amount) VALUES ($1, 'CURRENCY', $2, $3)`,
          [productId, g.currency, g.amount],
        );
      }
    }
  }

  private async replaceSkus(
    c: PoolClient,
    productId: string,
    skus: { ANDROID?: string; IOS?: string },
  ) {
    await c.query('DELETE FROM store_product_platform_skus WHERE product_id = $1', [productId]);
    for (const [platform, value] of Object.entries(skus)) {
      if (value)
        await c.query(
          'INSERT INTO store_product_platform_skus (product_id, platform, sku) VALUES ($1, $2, $3)',
          [productId, platform, value],
        );
    }
  }
}

/** Economy guardrails (BRD §12): no buying a currency with itself; coins are earned, never sold for money. */
function checkEconomy(priceCurrency: string, grants: { type: string; currency?: string }[]) {
  for (const g of grants) {
    if (g.type !== 'CURRENCY') continue;
    if (g.currency === priceCurrency)
      throw Invalid('A product cannot sell a currency for itself', [
        { path: 'grants', message: 'same currency' },
      ]);
    if (g.currency === 'COIN')
      throw Invalid('Coins are earned by playing and are not sold', [
        { path: 'grants', message: 'COIN grant' },
      ]);
  }
}
