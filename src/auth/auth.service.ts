import { Inject, Injectable } from '@nestjs/common';
import { randomInt, randomUUID, timingSafeEqual } from 'node:crypto';
import { PoolClient } from 'pg';
import { ApiError, Forbidden, Invalid, Unauthorized } from '../common/api-error';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';
import { randomToken, sha256, TokenService } from './token.service';

export interface AuthResult {
  userId: string;
  accessToken: string;
  accessTokenExpiresIn: number;
  refreshToken: string;
  refreshTokenExpiresAt: string;
  guest?: { deviceId: string; deviceSecret: string };
}

/** Cosmetics every new account starts with, equipped. */
const STARTER_COSMETICS = ['skin_rifle_default', 'outfit_default', 'banner_default'];

@Injectable()
export class AuthService {
  constructor(
    private readonly db: Db,
    private readonly tokens: TokenService,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {}

  async guest(
    body: { deviceId?: string; deviceSecret?: string; displayName?: string },
    userAgent: string | undefined,
  ): Promise<AuthResult> {
    if ((body.deviceId == null) !== (body.deviceSecret == null)) {
      throw Invalid('deviceId and deviceSecret must be sent together');
    }
    if (body.deviceId && body.deviceSecret)
      return this.guestSignIn(body.deviceId, body.deviceSecret, userAgent);
    return this.createGuest(body.displayName, userAgent);
  }

  private async guestSignIn(
    deviceId: string,
    secret: string,
    userAgent?: string,
  ): Promise<AuthResult> {
    const row = await this.db.one<{ user_id: string; credential_hash: Buffer; status: string }>(
      `SELECT i.user_id, i.credential_hash, u.status
         FROM auth_identities i JOIN users u ON u.id = i.user_id
        WHERE i.provider = 'GUEST' AND i.provider_subject = $1`,
      [deviceId],
    );
    // Same response for unknown device and wrong secret: no account enumeration.
    if (!row || !timingSafeEqual(row.credential_hash, sha256(secret))) {
      throw Unauthorized('INVALID_CREDENTIALS', 'Guest credentials are not valid');
    }
    assertActive(row.status);
    return this.db.tx(async (c) => {
      await c.query(
        `UPDATE auth_identities SET last_used_at = now() WHERE provider = 'GUEST' AND provider_subject = $1`,
        [deviceId],
      );
      return this.issueSession(c, row.user_id, randomUUID(), userAgent);
    });
  }

  private async createGuest(
    displayName: string | undefined,
    userAgent?: string,
  ): Promise<AuthResult> {
    const deviceId = randomUUID();
    const deviceSecret = randomToken();
    const name = displayName ?? `Player-${randomInt(1000, 10000)}`;

    const result = await this.db.tx(async (c) => {
      const { rows } = await c.query<{ id: string }>(
        'INSERT INTO users DEFAULT VALUES RETURNING id',
      );
      const userId = rows[0]!.id;
      await c.query(
        `INSERT INTO auth_identities (user_id, provider, provider_subject, credential_hash, last_used_at)
         VALUES ($1, 'GUEST', $2, $3, now())`,
        [userId, deviceId, sha256(deviceSecret)],
      );
      await c.query('INSERT INTO player_profiles (user_id, display_name) VALUES ($1, $2)', [
        userId,
        name,
      ]);
      await c.query(
        `INSERT INTO wallets (user_id, currency_code) VALUES ($1, 'COIN'), ($1, 'GEM')`,
        [userId],
      );
      await c.query(
        `INSERT INTO player_inventory (user_id, cosmetic_id, source, source_reference)
         SELECT $1, id, 'STARTER', 'signup' FROM cosmetics WHERE key = ANY($2)`,
        [userId, STARTER_COSMETICS],
      );
      await c.query(
        `INSERT INTO loadout_slots (user_id, slot, cosmetic_id)
         SELECT $1, kind, id FROM cosmetics WHERE key = ANY($2)`,
        [userId, STARTER_COSMETICS],
      );
      return this.issueSession(c, userId, randomUUID(), userAgent);
    });
    return { ...result, guest: { deviceId, deviceSecret } };
  }

  /** Rotates a refresh token. Presenting an already-rotated token revokes the whole sign-in (likely theft). */
  async refresh(refreshToken: string, userAgent?: string): Promise<AuthResult> {
    const outcome = await this.db.tx(async (c) => {
      const { rows } = await c.query<{
        id: string;
        user_id: string;
        family_id: string;
        expires_at: Date;
        revoke_reason: string | null;
        status: string;
      }>(
        `SELECT s.id, s.user_id, s.family_id, s.expires_at, s.revoke_reason, u.status
           FROM player_sessions s JOIN users u ON u.id = s.user_id
          WHERE s.refresh_token_hash = $1 FOR UPDATE OF s`,
        [sha256(refreshToken)],
      );
      const s = rows[0];
      if (!s) return { error: Unauthorized('INVALID_REFRESH_TOKEN', 'Refresh token is not valid') };
      if (s.revoke_reason === 'ROTATED') {
        await c.query(
          `UPDATE player_sessions SET revoked_at = now(), revoke_reason = 'REUSE_DETECTED'
            WHERE family_id = $1 AND revoked_at IS NULL`,
          [s.family_id],
        );
        await c.query(
          `INSERT INTO admin_audit_logs (actor, action, entity_type, entity_id, reason)
           VALUES ('system', 'session.reuse_detected', 'user', $1, 'rotated refresh token presented again; sign-in revoked')`,
          [s.user_id],
        );
        return { error: Unauthorized('REFRESH_TOKEN_REUSED', 'Session revoked; sign in again') };
      }
      if (s.revoke_reason || s.expires_at.getTime() <= Date.now()) {
        return { error: Unauthorized('INVALID_REFRESH_TOKEN', 'Refresh token is not valid') };
      }
      assertActive(s.status);
      await c.query(
        `UPDATE player_sessions SET rotated_at = now(), revoked_at = now(), revoke_reason = 'ROTATED' WHERE id = $1`,
        [s.id],
      );
      return { result: await this.issueSession(c, s.user_id, s.family_id, userAgent) };
    });
    // Errors are thrown after commit so the reuse revocation is kept.
    if ('error' in outcome) throw outcome.error;
    return outcome.result;
  }

  async logout(refreshToken: string): Promise<void> {
    await this.db.query(
      `UPDATE player_sessions SET revoked_at = now(), revoke_reason = 'LOGOUT'
        WHERE refresh_token_hash = $1 AND revoked_at IS NULL`,
      [sha256(refreshToken)],
    );
  }

  platform(): never {
    throw new ApiError(
      501,
      'NOT_IMPLEMENTED',
      'Platform sign-in is not configured yet (Google Play Games / Apple credentials pending)',
    );
  }

  private async issueSession(
    c: PoolClient,
    userId: string,
    familyId: string,
    userAgent?: string,
  ): Promise<AuthResult> {
    const refreshToken = randomToken();
    const expires = new Date(Date.now() + this.config.REFRESH_TOKEN_TTL_DAYS * 86_400_000);
    await c.query(
      `INSERT INTO player_sessions (user_id, family_id, refresh_token_hash, expires_at, user_agent)
       VALUES ($1, $2, $3, $4, $5)`,
      [userId, familyId, sha256(refreshToken), expires, userAgent?.slice(0, 200) ?? null],
    );
    return {
      userId,
      accessToken: await this.tokens.signAccess(userId),
      accessTokenExpiresIn: this.tokens.accessTtlSeconds,
      refreshToken,
      refreshTokenExpiresAt: expires.toISOString(),
    };
  }
}

/** Accounts pending deletion can still sign in, so the player can cancel within the grace period. */
export function assertActive(status: string): void {
  if (status !== 'ACTIVE' && status !== 'PENDING_DELETION')
    throw Forbidden('ACCOUNT_RESTRICTED', 'This account cannot sign in');
}
