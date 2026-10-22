import { Inject, Injectable } from '@nestjs/common';
import { createHash, createHmac, timingSafeEqual } from 'node:crypto';
import { createRemoteJWKSet, jwtVerify } from 'jose';
import { ApiError, NotFound, Unauthorized } from '../common/api-error';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';
import { StoreService } from './store.service';
import { AppleTransaction, StorePlatform, verifyAppleJws } from './verifiers';

type Outcome = 'APPLIED' | 'IGNORED' | 'UNMATCHED' | 'REJECTED';

/**
 * Platform purchase notifications (BRD §9 "Payments": refunds and chargebacks are reflected in the game).
 * Each event is authenticated, then processed at most once by (platform, event id) and recorded in purchase_events.
 * Refund handling is itself idempotent, so concurrent redeliveries are harmless too.
 */
@Injectable()
export class WebhooksService {
  private googleKeys?: ReturnType<typeof createRemoteJWKSet>;

  constructor(
    private readonly db: Db,
    private readonly store: StoreService,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {}

  // ---- Google Play Real-time developer notifications (Pub/Sub push) ----

  async google(authorization: string | undefined, raw: Buffer, body: unknown): Promise<Outcome> {
    const c = this.config;
    if (!c.GOOGLE_RTDN_AUDIENCE || !c.GOOGLE_RTDN_SERVICE_ACCOUNT || !c.GOOGLE_PLAY_PACKAGE_NAME)
      throw NotFound('Endpoint');
    const token = /^Bearer (.+)$/.exec(authorization ?? '')?.[1];
    if (!token) throw Unauthorized();
    this.googleKeys ??= createRemoteJWKSet(new URL(c.GOOGLE_RTDN_JWKS_URL));
    try {
      const { payload } = await jwtVerify(token, this.googleKeys, {
        issuer: ['https://accounts.google.com', 'accounts.google.com'],
        audience: c.GOOGLE_RTDN_AUDIENCE,
      });
      if (payload.email !== c.GOOGLE_RTDN_SERVICE_ACCOUNT || payload.email_verified !== true)
        throw new Error('sender');
    } catch {
      throw Unauthorized('BAD_SIGNATURE', 'Push token is not valid');
    }

    const message = (body as { message?: { data?: string; messageId?: string } })?.message;
    if (!message?.messageId || !message.data)
      throw new ApiError(422, 'VALIDATION_ERROR', 'Not a Pub/Sub push message');
    let n: {
      packageName?: string;
      testNotification?: unknown;
      oneTimeProductNotification?: { notificationType: number; sku: string };
      voidedPurchaseNotification?: { orderId: string; productType: number; refundType: number };
    };
    try {
      n = JSON.parse(Buffer.from(message.data, 'base64').toString('utf8'));
    } catch {
      throw new ApiError(422, 'VALIDATION_ERROR', 'Notification data is not JSON');
    }
    return this.once('ANDROID', message.messageId, raw, async () => {
      if (n.packageName !== c.GOOGLE_PLAY_PACKAGE_NAME)
        return ['wrong-package', 'REJECTED', 'packageName does not match'];
      if (n.voidedPurchaseNotification) {
        return this.refundTransaction(
          'ANDROID',
          n.voidedPurchaseNotification.orderId,
          'voided-purchase',
          'platform:google',
        );
      }
      if (n.oneTimeProductNotification) {
        // Purchases are fulfilled when the client sends the token (or via restore); nothing to do here.
        return [`one-time-${n.oneTimeProductNotification.notificationType}`, 'IGNORED', null];
      }
      return [n.testNotification ? 'test' : 'other', 'IGNORED', null];
    });
  }

  // ---- App Store Server Notifications v2 ----

  async apple(raw: Buffer, body: unknown): Promise<Outcome> {
    const root = this.config.APPLE_ROOT_CERT_PEM;
    if (!root || !this.config.APPLE_BUNDLE_ID) throw NotFound('Endpoint');
    const signedPayload = (body as { signedPayload?: unknown })?.signedPayload;
    if (typeof signedPayload !== 'string')
      throw new ApiError(422, 'VALIDATION_ERROR', 'signedPayload is required');
    let n: {
      notificationType: string;
      subtype?: string;
      notificationUUID: string;
      data?: { bundleId?: string; signedTransactionInfo?: string };
    };
    try {
      n = await verifyAppleJws(signedPayload, root);
    } catch {
      throw Unauthorized('BAD_SIGNATURE', 'Notification signature is not valid');
    }
    return this.once('IOS', n.notificationUUID, raw, async () => {
      const data = n.data;
      if (!data || data.bundleId !== this.config.APPLE_BUNDLE_ID)
        return [n.notificationType, 'REJECTED', 'bundleId does not match'];
      if (n.notificationType !== 'REFUND' && n.notificationType !== 'REVOKE')
        return [n.notificationType, 'IGNORED', null];
      if (!data.signedTransactionInfo) return [n.notificationType, 'REJECTED', 'no transaction'];
      const t = await verifyAppleJws<AppleTransaction>(data.signedTransactionInfo, root);
      return this.refundTransaction('IOS', t.transactionId, n.notificationType, 'platform:apple');
    });
  }

  // ---- Dev notifications (local testing of the refund path) ----

  async dev(signature: string | undefined, raw: Buffer, body: unknown): Promise<Outcome> {
    const secret = this.config.DEV_PURCHASES_ENABLED ? this.config.DEV_PURCHASE_SECRET : undefined;
    if (!secret) throw NotFound('Endpoint');
    const expected = createHmac('sha256', secret).update(raw).digest();
    const given = Buffer.from(signature ?? '', 'hex');
    if (given.length !== expected.length || !timingSafeEqual(given, expected))
      throw Unauthorized('BAD_SIGNATURE', 'Dev notification signature is not valid');
    const e = body as { eventId?: unknown; type?: unknown; transactionId?: unknown };
    if (typeof e.eventId !== 'string' || typeof e.transactionId !== 'string' || e.type !== 'REFUND')
      throw new ApiError(
        422,
        'VALIDATION_ERROR',
        'Expected {eventId, type: REFUND, transactionId}',
      );
    return this.once('DEV', e.eventId, raw, () =>
      this.refundTransaction('DEV', e.transactionId as string, 'REFUND', 'platform:dev'),
    );
  }

  // ---- Shared ----

  private async refundTransaction(
    platform: StorePlatform,
    transactionId: string,
    eventType: string,
    actor: string,
  ): Promise<[string, Outcome, string | null, string?]> {
    const orderId = await this.store.orderForTransaction(platform, transactionId);
    if (!orderId)
      return [eventType, 'UNMATCHED', `no order for transaction ${transactionId}`.slice(0, 300)];
    const r = await this.store.refund(orderId, actor, `${platform} ${eventType}`);
    return [
      eventType,
      'APPLIED',
      r.alreadyRefunded ? 'already refunded' : `shortfall ${r.shortfall}`,
      orderId,
    ];
  }

  private async once(
    platform: StorePlatform,
    eventId: string,
    raw: Buffer,
    handle: () => Promise<[string, Outcome, string | null, string?]>,
  ): Promise<Outcome> {
    const seen = await this.db.one<{ outcome: Outcome }>(
      'SELECT outcome FROM purchase_events WHERE platform = $1 AND provider_event_id = $2',
      [platform, eventId],
    );
    if (seen) return seen.outcome;
    const [eventType, outcome, detail, orderId] = await handle();
    await this.db.query(
      `INSERT INTO purchase_events (platform, provider_event_id, event_type, order_id, payload_sha256, outcome, detail)
       VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT (platform, provider_event_id) DO NOTHING`,
      [
        platform,
        eventId.slice(0, 200),
        eventType.slice(0, 60),
        orderId ?? null,
        createHash('sha256').update(raw).digest(),
        outcome,
        detail,
      ],
    );
    return outcome;
  }
}
