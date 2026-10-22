import { Body, Controller, Get, HttpCode, Param, Post, Put, Req, UseGuards } from '@nestjs/common';
import { z } from 'zod';
import { Conflict, NotFound, parse } from '../common/api-error';
import { RateLimit, RateLimitGuard } from '../common/guards';
import { parseUuid } from '../common/schemas';
import { Db } from '../database/database.module';
import {
  ADMIN_ROLES,
  AdminAuthService,
  AdminGuard,
  AdminPrincipal,
  AdminRequest,
  CurrentAdmin,
  RequestId,
  RequirePermission,
} from './admin-auth';

const email = z.string().email().max(200);
const loginBody = z
  .object({ email, password: z.string().min(1).max(200), code: z.string().regex(/^\d{6}$/) })
  .strict();
const roles = z.array(z.enum(ADMIN_ROLES)).min(1).max(ADMIN_ROLES.length);
const createAdminBody = z
  .object({
    email,
    displayName: z.string().min(1).max(60),
    password: z.string().min(12).max(200),
    roles,
  })
  .strict();

@Controller('admin/auth')
@UseGuards(RateLimitGuard)
export class AdminAuthController {
  constructor(private readonly auth: AdminAuthService) {}

  @Post('login')
  @HttpCode(200)
  @RateLimit({ limit: 10, windowSeconds: 60, key: 'ip' })
  login(@Req() req: AdminRequest, @Body() body: unknown) {
    const b = parse(loginBody, body);
    return this.auth.login(b.email, b.password, b.code, req.header('user-agent'), req.requestId);
  }

  @Post('logout')
  @HttpCode(204)
  @UseGuards(AdminGuard)
  async logout(@Req() req: AdminRequest): Promise<void> {
    await this.auth.logout(req.header('authorization')!.slice('Admin '.length));
  }
}

@Controller('admin')
@UseGuards(AdminGuard)
export class AdminController {
  constructor(
    private readonly db: Db,
    private readonly auth: AdminAuthService,
  ) {}

  @Get('me')
  me(@CurrentAdmin() admin: AdminPrincipal) {
    return admin;
  }

  /** Headline numbers for the portal's first screen. Visible to every admin role. */
  @Get('overview')
  async overview() {
    const row = await this.db.one<Record<string, number>>(
      `SELECT
         (SELECT count(*)::int FROM users WHERE status <> 'DELETED') AS players,
         (SELECT count(DISTINCT user_id)::int FROM player_sessions WHERE created_at > now() - interval '1 day') AS "activePlayers24h",
         (SELECT count(*)::int FROM matches WHERE created_at > now() - interval '1 day') AS "matches24h",
         (SELECT count(*)::int FROM matches WHERE status IN ('FORMING', 'ACTIVE')) AS "liveMatches",
         (SELECT count(*)::int FROM game_servers WHERE last_heartbeat_at > now() - interval '30 seconds') AS "serversOnline",
         (SELECT count(*)::int FROM player_reports WHERE status IN ('OPEN', 'UNDER_REVIEW')) AS "openReports",
         (SELECT count(*)::int FROM store_products WHERE status = 'PENDING_APPROVAL') AS "productsAwaitingApproval",
         (SELECT count(*)::int FROM orders WHERE status = 'PENDING') AS "pendingOrders",
         (SELECT COALESCE(sum(amount_minor), 0)::bigint FROM orders
           WHERE currency_code = 'INR' AND status = 'FULFILLED' AND platform <> 'DEV' AND fulfilled_at > now() - interval '30 days') AS "revenueInrMinor30d",
         (SELECT count(*)::int FROM orders WHERE status = 'REFUNDED' AND refunded_at > now() - interval '30 days') AS "refunds30d",
         (SELECT count(*)::int FROM wallet_ledger_drift) AS "walletsWithLedgerDrift"`,
    );
    return Object.fromEntries(Object.entries(row!).map(([k, v]) => [k, Number(v)]));
  }

  // ---- Admin accounts ----

  @Get('admins')
  @RequirePermission('admins.manage')
  async listAdmins() {
    const rows = await this.db.query<{ id: string }>(
      'SELECT id FROM admin_users ORDER BY created_at',
    );
    const items = [];
    for (const r of rows) {
      const p = await this.auth.principal(r.id);
      const s = await this.db.one<{ status: string; last_login_at: Date | null }>(
        'SELECT status, last_login_at FROM admin_users WHERE id = $1',
        [r.id],
      );
      items.push({
        id: p.id,
        email: p.email,
        displayName: p.displayName,
        roles: p.roles,
        status: s!.status,
        lastLoginAt: s!.last_login_at?.toISOString() ?? null,
      });
    }
    return { items };
  }

  @Post('admins')
  @HttpCode(201)
  @RequirePermission('admins.manage')
  createAdmin(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Body() body: unknown,
  ) {
    return this.auth.createAdmin(parse(createAdminBody, body), admin.actor, requestId);
  }

  @Put('admins/:adminId/roles')
  @RequirePermission('admins.manage')
  async setRoles(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('adminId') adminId: string,
    @Body() body: unknown,
  ) {
    const id = parseUuid(adminId, 'adminId');
    const r = parse(z.object({ roles, reason: z.string().min(3).max(500) }).strict(), body);
    if (id === admin.id && !r.roles.includes('SUPER_ADMIN') && admin.roles.includes('SUPER_ADMIN'))
      throw Conflict('SELF_DEMOTION', 'You cannot remove your own SUPER_ADMIN role');
    await this.db.tx(async (c) => {
      const { rowCount } = await c.query('SELECT 1 FROM admin_users WHERE id = $1 FOR UPDATE', [
        id,
      ]);
      if (!rowCount) throw NotFound('Admin');
      await this.auth.setRoles(c, id, r.roles);
      await this.auth.audit(
        c,
        admin.actor,
        'admin.roles',
        'admin',
        id,
        r.reason,
        { roles: r.roles },
        requestId,
      );
    });
    return this.auth.principal(id);
  }

  @Post('admins/:adminId/disable')
  @HttpCode(200)
  @RequirePermission('admins.manage')
  async disable(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('adminId') adminId: string,
    @Body() body: unknown,
  ) {
    const id = parseUuid(adminId, 'adminId');
    const { reason } = parse(z.object({ reason: z.string().min(3).max(500) }).strict(), body);
    if (id === admin.id) throw Conflict('SELF_DISABLE', 'You cannot disable your own account');
    await this.db.tx(async (c) => {
      const { rowCount } = await c.query(
        `UPDATE admin_users SET status = 'DISABLED' WHERE id = $1`,
        [id],
      );
      if (!rowCount) throw NotFound('Admin');
      await c.query(
        'UPDATE admin_sessions SET revoked_at = now() WHERE admin_user_id = $1 AND revoked_at IS NULL',
        [id],
      );
      await this.auth.audit(c, admin.actor, 'admin.disable', 'admin', id, reason, null, requestId);
    });
    return { id, status: 'DISABLED' };
  }
}
