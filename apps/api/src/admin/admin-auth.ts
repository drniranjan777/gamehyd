import {
  CanActivate,
  createParamDecorator,
  ExecutionContext,
  Inject,
  Injectable,
  SetMetadata,
} from '@nestjs/common';
import { Reflector } from '@nestjs/core';
import { randomBytes } from 'node:crypto';
import { PoolClient } from 'pg';
import { ApiError, Conflict, Forbidden, Invalid, Unauthorized } from '../common/api-error';
import { AuthedRequest } from '../common/guards';
import { sha256 } from '../auth/token.service';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';
import {
  base32Encode,
  decryptSecret,
  DUMMY_PASSWORD_HASH,
  encryptSecret,
  hashPassword,
  otpauthUrl,
  verifyPassword,
  verifyTotp,
} from './admin-crypto';

export const ADMIN_ROLES = [
  'SUPPORT',
  'MODERATOR',
  'ECONOMY_OPERATOR',
  'ANALYST',
  'SUPER_ADMIN',
] as const;
export type AdminRole = (typeof ADMIN_ROLES)[number];

export interface AdminPrincipal {
  id: string;
  email: string;
  displayName: string;
  roles: string[];
  permissions: string[];
  /** The audit-log actor string. */
  actor: string;
}

export interface AdminRequest extends AuthedRequest {
  admin?: AdminPrincipal;
}

const MAX_FAILED_LOGINS = 5;
const LOCKOUT_MINUTES = 15;

/**
 * Admin sign-in (BRD §9 "Administration": MFA, RBAC, audit). Email + password + TOTP every time; five failures lock
 * the account for 15 minutes; a TOTP code can be used once. Sessions are opaque tokens stored as hashes.
 */
@Injectable()
export class AdminAuthService {
  constructor(
    private readonly db: Db,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {}

  async login(
    email: string,
    password: string,
    code: string,
    userAgent?: string,
    requestId?: string,
  ) {
    const fail = () => Unauthorized('INVALID_CREDENTIALS', 'Email, password or code is not valid');
    const a = await this.db.one<{
      id: string;
      password_hash: string;
      totp_secret_enc: Buffer;
      status: string;
      locked_until: Date | null;
      last_totp_step: string;
    }>(
      'SELECT id, password_hash, totp_secret_enc, status, locked_until, last_totp_step FROM admin_users WHERE email = $1',
      [email.toLowerCase()],
    );
    const passwordOk = await verifyPassword(password, a?.password_hash ?? DUMMY_PASSWORD_HASH);
    if (!a) throw fail();
    if (a.locked_until && a.locked_until.getTime() > Date.now())
      throw new ApiError(423, 'ACCOUNT_LOCKED', 'Too many failed attempts; try again later');
    const step = passwordOk
      ? verifyTotp(decryptSecret(this.config.ADMIN_TOTP_KEY_HEX, a.totp_secret_enc), code)
      : undefined;
    const ok =
      passwordOk && step !== undefined && step > Number(a.last_totp_step) && a.status === 'ACTIVE';
    if (!ok) {
      await this.db.query(
        `UPDATE admin_users SET failed_logins = failed_logins + 1,
           locked_until = CASE WHEN failed_logins + 1 >= $2 THEN now() + make_interval(mins => $3::int) ELSE locked_until END
         WHERE id = $1`,
        [a.id, MAX_FAILED_LOGINS, LOCKOUT_MINUTES],
      );
      await this.audit(
        this.db,
        `admin:${a.id}`,
        'admin.login_failed',
        'admin',
        a.id,
        null,
        null,
        requestId,
      );
      throw fail();
    }
    const token = randomBytes(32).toString('base64url');
    const expiresAt = new Date(Date.now() + this.config.ADMIN_SESSION_HOURS * 3600_000);
    await this.db.tx(async (c) => {
      await c.query(
        `UPDATE admin_users SET failed_logins = 0, locked_until = NULL, last_totp_step = $2, last_login_at = now() WHERE id = $1`,
        [a.id, step],
      );
      await c.query(
        'INSERT INTO admin_sessions (admin_user_id, token_hash, expires_at, user_agent) VALUES ($1, $2, $3, $4)',
        [a.id, sha256(token), expiresAt, userAgent?.slice(0, 200) ?? null],
      );
      await this.audit(c, `admin:${a.id}`, 'admin.login', 'admin', a.id, null, null, requestId);
    });
    return { token, expiresAt: expiresAt.toISOString(), admin: await this.principal(a.id) };
  }

  async logout(token: string): Promise<void> {
    await this.db.query(
      'UPDATE admin_sessions SET revoked_at = now() WHERE token_hash = $1 AND revoked_at IS NULL',
      [sha256(token)],
    );
  }

  async authenticate(token: string): Promise<AdminPrincipal> {
    const s = await this.db.one<{ admin_user_id: string }>(
      `SELECT s.admin_user_id FROM admin_sessions s JOIN admin_users a ON a.id = s.admin_user_id
        WHERE s.token_hash = $1 AND s.revoked_at IS NULL AND s.expires_at > now() AND a.status = 'ACTIVE'`,
      [sha256(token)],
    );
    if (!s) throw Unauthorized('INVALID_TOKEN', 'Admin session is invalid or expired');
    return this.principal(s.admin_user_id);
  }

  async principal(id: string): Promise<AdminPrincipal> {
    const a = await this.db.one<{
      id: string;
      email: string;
      display_name: string;
      roles: string[];
      permissions: string[];
    }>(
      `SELECT a.id, a.email, a.display_name,
              COALESCE(array_agg(DISTINCT r.role_key) FILTER (WHERE r.role_key IS NOT NULL), '{}') AS roles,
              COALESCE(array_agg(DISTINCT p.permission_key) FILTER (WHERE p.permission_key IS NOT NULL), '{}') AS permissions
         FROM admin_users a
         LEFT JOIN admin_user_roles r ON r.admin_user_id = a.id
         LEFT JOIN admin_role_permissions p ON p.role_key = r.role_key
        WHERE a.id = $1 GROUP BY a.id`,
      [id],
    );
    if (!a) throw Unauthorized();
    return {
      id: a.id,
      email: a.email,
      displayName: a.display_name,
      roles: a.roles.sort(),
      permissions: a.permissions.sort(),
      actor: `admin:${a.id}`,
    };
  }

  /** Creates an admin. The TOTP secret is returned once, to be enrolled in an authenticator app. */
  async createAdmin(
    input: { email: string; displayName: string; password: string; roles: string[] },
    actor: string,
    requestId?: string,
  ) {
    if (input.password.length < 12)
      throw Invalid('Password must be at least 12 characters', [
        { path: 'password', message: 'too short' },
      ]);
    const secret = randomBytes(20);
    const passwordHash = await hashPassword(input.password);
    const email = input.email.toLowerCase();
    try {
      const id = await this.db.tx(async (c) => {
        const { rows } = await c.query<{ id: string }>(
          `INSERT INTO admin_users (email, display_name, password_hash, totp_secret_enc) VALUES ($1, $2, $3, $4) RETURNING id`,
          [
            email,
            input.displayName,
            passwordHash,
            encryptSecret(this.config.ADMIN_TOTP_KEY_HEX, secret),
          ],
        );
        const newId = rows[0]!.id;
        await this.setRoles(c, newId, input.roles);
        await this.audit(
          c,
          actor,
          'admin.create',
          'admin',
          newId,
          null,
          { email, roles: input.roles },
          requestId,
        );
        return newId;
      });
      const totpSecret = base32Encode(secret);
      return {
        admin: await this.principal(id),
        totpSecret,
        otpauthUrl: otpauthUrl(email, totpSecret),
      };
    } catch (err) {
      if ((err as { code?: string }).code === '23505')
        throw Conflict('ADMIN_EXISTS', 'An admin with this email exists');
      throw err;
    }
  }

  async setRoles(c: PoolClient, adminId: string, roles: string[]): Promise<void> {
    await c.query('DELETE FROM admin_user_roles WHERE admin_user_id = $1', [adminId]);
    for (const role of new Set(roles)) {
      await c.query('INSERT INTO admin_user_roles (admin_user_id, role_key) VALUES ($1, $2)', [
        adminId,
        role,
      ]);
    }
  }

  async audit(
    q: Db | PoolClient,
    actor: string,
    action: string,
    entityType: string,
    entityId: string,
    reason: string | null,
    details: object | null,
    requestId?: string,
  ): Promise<void> {
    const sql = `INSERT INTO admin_audit_logs (actor, action, entity_type, entity_id, reason, details, request_id)
       VALUES ($1, $2, $3, $4, $5, $6, $7)`;
    const params = [actor, action, entityType, entityId, reason, details, requestId ?? null];
    if (q instanceof Db) await q.query(sql, params);
    else await q.query(sql, params);
  }
}

// ---- Guard and decorators ----

const PERMISSION = 'rwbr:admin-permission';
/** Requires the named permission. Without it, any signed-in admin may call the endpoint. */
export const RequirePermission = (permission: string) => SetMetadata(PERMISSION, permission);

/** Requires `Authorization: Admin <token>` and, when declared, a permission. Sets req.admin. */
@Injectable()
export class AdminGuard implements CanActivate {
  constructor(
    private readonly auth: AdminAuthService,
    private readonly reflector: Reflector,
  ) {}

  async canActivate(ctx: ExecutionContext): Promise<boolean> {
    const req = ctx.switchToHttp().getRequest<AdminRequest>();
    const match = /^Admin ([A-Za-z0-9_-]{20,100})$/.exec(req.header('authorization') ?? '');
    if (!match) throw Unauthorized();
    req.admin = await this.auth.authenticate(match[1]!);
    const needed = this.reflector.getAllAndOverride<string | undefined>(PERMISSION, [
      ctx.getHandler(),
      ctx.getClass(),
    ]);
    if (needed && !req.admin.permissions.includes(needed))
      throw Forbidden('PERMISSION_DENIED', `This action needs the ${needed} permission`);
    return true;
  }
}

export const CurrentAdmin = createParamDecorator(
  (_: unknown, ctx: ExecutionContext): AdminPrincipal => {
    const admin = ctx.switchToHttp().getRequest<AdminRequest>().admin;
    if (!admin) throw Unauthorized();
    return admin;
  },
);

export const RequestId = createParamDecorator(
  (_: unknown, ctx: ExecutionContext): string | undefined =>
    ctx.switchToHttp().getRequest<AuthedRequest>().requestId,
);
