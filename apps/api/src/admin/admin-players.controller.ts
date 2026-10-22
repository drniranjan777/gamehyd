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
import { z } from 'zod';
import { Conflict, Invalid, NotFound, parse } from '../common/api-error';
import { parseUuid, uuid } from '../common/schemas';
import { Db } from '../database/database.module';
import { levelFor } from '../player/progression';
import { OrderRow, orderView } from '../store/store.service';
import {
  AdminAuthService,
  AdminGuard,
  AdminPrincipal,
  CurrentAdmin,
  RequestId,
  RequirePermission,
} from './admin-auth';

const sanctionBody = z
  .object({
    action: z.enum(['WARN', 'SUSPEND', 'BAN', 'UNBAN']),
    reason: z.string().min(3).max(500),
    expiresAt: z.string().datetime().optional(),
    reportId: uuid.optional(),
  })
  .strict();

const reportPatch = z
  .object({
    status: z.enum(['OPEN', 'UNDER_REVIEW', 'ACTIONED', 'DISMISSED']),
    note: z.string().min(3).max(500),
  })
  .strict();

const listQuery = z
  .object({
    status: z
      .string()
      .regex(/^[A-Z_]{3,20}$/)
      .optional(),
    limit: z.coerce.number().int().min(1).max(100).default(50),
  })
  .strict();

@Controller('admin')
@UseGuards(AdminGuard)
export class AdminPlayersController {
  constructor(
    private readonly db: Db,
    private readonly auth: AdminAuthService,
  ) {}

  @Get('players')
  @RequirePermission('players.read')
  async search(@Query() query: unknown) {
    const { q } = parse(z.object({ q: z.string().min(1).max(60) }).strict(), query);
    const byId = uuid.safeParse(q).success;
    const rows = await this.db.query<{
      user_id: string;
      display_name: string;
      status: string;
      xp: string;
      created_at: Date;
    }>(
      `SELECT p.user_id, p.display_name, u.status, p.xp, u.created_at
         FROM player_profiles p JOIN users u ON u.id = p.user_id
        WHERE ${byId ? 'p.user_id = $1::uuid' : `p.display_name ILIKE '%' || $1 || '%' ESCAPE '\\'`}
        ORDER BY u.created_at DESC LIMIT 50`,
      [byId ? q : q.replace(/[\\%_]/g, (m) => `\\${m}`)],
    );
    return {
      items: rows.map((r) => ({
        playerId: r.user_id,
        displayName: r.display_name,
        status: r.status,
        level: levelFor(Number(r.xp)),
        createdAt: r.created_at.toISOString(),
      })),
    };
  }

  @Get('players/:playerId')
  @RequirePermission('players.read')
  async detail(@Param('playerId') playerId: string) {
    const id = parseUuid(playerId, 'playerId');
    const p = await this.db.one<{
      display_name: string;
      status: string;
      xp: string;
      matches_played: number;
      wins: number;
      kills: number;
      created_at: Date;
    }>(
      `SELECT p.display_name, u.status, p.xp, p.matches_played, p.wins, p.kills, u.created_at
         FROM users u JOIN player_profiles p ON p.user_id = u.id WHERE u.id = $1`,
      [id],
    );
    if (!p) throw NotFound('Player');
    const [wallets, inventory, orders, sanctions, reports, identities] = await Promise.all([
      this.db.query<{ currency_code: string; balance: string }>(
        'SELECT currency_code, balance FROM wallets WHERE user_id = $1',
        [id],
      ),
      this.db.query<{ key: string; source: string }>(
        'SELECT c.key, i.source FROM player_inventory i JOIN cosmetics c ON c.id = i.cosmetic_id WHERE i.user_id = $1 ORDER BY c.key',
        [id],
      ),
      this.db.query<OrderRow>(
        `SELECT o.*, p.key AS product_key FROM orders o JOIN store_products p ON p.id = o.product_id
          WHERE o.user_id = $1 ORDER BY o.created_at DESC LIMIT 20`,
        [id],
      ),
      this.db.query<{
        id: string;
        action: string;
        reason: string;
        expires_at: Date | null;
        admin_email: string;
        created_at: Date;
      }>(
        `SELECT m.id, m.action, m.reason, m.expires_at, a.email AS admin_email, m.created_at
           FROM moderation_actions m JOIN admin_users a ON a.id = m.admin_user_id
          WHERE m.user_id = $1 ORDER BY m.created_at DESC LIMIT 50`,
        [id],
      ),
      this.db.query<{ id: string; reason: string; status: string; created_at: Date }>(
        'SELECT id, reason, status, created_at FROM player_reports WHERE reported_user_id = $1 ORDER BY created_at DESC LIMIT 20',
        [id],
      ),
      this.db.query<{ provider: string; last_used_at: Date | null }>(
        'SELECT provider, last_used_at FROM auth_identities WHERE user_id = $1',
        [id],
      ),
    ]);
    return {
      playerId: id,
      displayName: p.display_name,
      status: p.status,
      level: levelFor(Number(p.xp)),
      xp: Number(p.xp),
      matchesPlayed: p.matches_played,
      wins: p.wins,
      kills: p.kills,
      createdAt: p.created_at.toISOString(),
      signIn: identities.map((i) => ({
        provider: i.provider,
        lastUsedAt: i.last_used_at?.toISOString() ?? null,
      })),
      wallets: Object.fromEntries(wallets.map((w) => [w.currency_code, Number(w.balance)])),
      inventory: inventory.map((i) => ({ cosmeticKey: i.key, source: i.source })),
      orders: orders.map((o) => orderView(o)),
      sanctions: sanctions.map((s) => ({
        id: s.id,
        action: s.action,
        reason: s.reason,
        expiresAt: s.expires_at?.toISOString() ?? null,
        by: s.admin_email,
        createdAt: s.created_at.toISOString(),
      })),
      reports: reports.map((r) => ({
        reportId: r.id,
        reason: r.reason,
        status: r.status,
        createdAt: r.created_at.toISOString(),
      })),
    };
  }

  /** Warn, suspend (until a time), ban or lift a sanction. Suspensions and bans end the player's sessions. */
  @Post('players/:playerId/sanctions')
  @HttpCode(201)
  @RequirePermission('players.sanction')
  async sanction(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('playerId') playerId: string,
    @Body() body: unknown,
  ) {
    const id = parseUuid(playerId, 'playerId');
    const s = parse(sanctionBody, body);
    const expires = s.expiresAt ? new Date(s.expiresAt) : null;
    if (s.action === 'SUSPEND') {
      if (
        !expires ||
        expires.getTime() <= Date.now() ||
        expires.getTime() > Date.now() + 366 * 86400_000
      )
        throw Invalid('A suspension needs an end time within a year', [
          { path: 'expiresAt', message: 'required, future, ≤ 1 year' },
        ]);
    } else if (expires) {
      throw Invalid('Only suspensions have an end time', [
        { path: 'expiresAt', message: 'not allowed for this action' },
      ]);
    }
    return this.db.tx(async (c) => {
      const { rows } = await c.query<{ status: string }>(
        'SELECT status FROM users WHERE id = $1 FOR UPDATE',
        [id],
      );
      const status = rows[0]?.status;
      if (!status) throw NotFound('Player');
      if (status === 'DELETED') throw Conflict('PLAYER_DELETED', 'This account was deleted');
      const next =
        s.action === 'SUSPEND'
          ? 'SUSPENDED'
          : s.action === 'BAN'
            ? 'BANNED'
            : s.action === 'UNBAN'
              ? 'ACTIVE'
              : status;
      if (s.action === 'UNBAN' && status !== 'SUSPENDED' && status !== 'BANNED')
        throw Conflict('NOT_SANCTIONED', 'The player is not suspended or banned');
      if (s.action === 'SUSPEND' && status === 'BANNED')
        throw Conflict('ALREADY_BANNED', 'Lift the ban first');
      if (s.reportId) {
        const r = await c.query(
          `UPDATE player_reports SET status = 'ACTIONED' WHERE id = $1 AND reported_user_id = $2`,
          [s.reportId, id],
        );
        if (!r.rowCount) throw NotFound('Report');
      }
      const { rows: inserted } = await c.query<{ id: string; created_at: Date }>(
        `INSERT INTO moderation_actions (user_id, action, reason, report_id, expires_at, admin_user_id)
         VALUES ($1, $2, $3, $4, $5, $6) RETURNING id, created_at`,
        [id, s.action, s.reason, s.reportId ?? null, expires, admin.id],
      );
      if (next !== status) await c.query('UPDATE users SET status = $2 WHERE id = $1', [id, next]);
      if (next === 'SUSPENDED' || next === 'BANNED') {
        await c.query(
          `UPDATE player_sessions SET revoked_at = now(), revoke_reason = 'ADMIN' WHERE user_id = $1 AND revoked_at IS NULL`,
          [id],
        );
        await c.query('DELETE FROM matchmaking_queue WHERE user_id = $1', [id]);
      }
      await this.auth.audit(
        c,
        admin.actor,
        `player.${s.action.toLowerCase()}`,
        'user',
        id,
        s.reason,
        {
          from: status,
          to: next,
          expiresAt: s.expiresAt ?? null,
          reportId: s.reportId ?? null,
        },
        requestId,
      );
      return {
        id: inserted[0]!.id,
        action: s.action,
        playerStatus: next,
        expiresAt: expires?.toISOString() ?? null,
        createdAt: inserted[0]!.created_at.toISOString(),
      };
    });
  }

  // ---- Matches ----

  @Get('matches')
  @RequirePermission('matches.read')
  async matches(@Query() query: unknown) {
    const q = parse(listQuery, query);
    const rows = await this.db.query<{
      id: string;
      status: string;
      map_version: string;
      region: string;
      players: number;
      winner_user_id: string | null;
      duration_seconds: number | null;
      created_at: Date;
    }>(
      `SELECT m.id, m.status, m.map_version, m.region, m.winner_user_id, m.duration_seconds, m.created_at,
              (SELECT count(*)::int FROM match_participants p WHERE p.match_id = m.id) AS players
         FROM matches m WHERE ($1::text IS NULL OR m.status = $1) ORDER BY m.created_at DESC LIMIT $2`,
      [q.status ?? null, q.limit],
    );
    return {
      items: rows.map((m) => ({
        matchId: m.id,
        status: m.status,
        mapVersion: m.map_version,
        region: m.region,
        players: m.players,
        winnerPlayerId: m.winner_user_id,
        durationSeconds: m.duration_seconds,
        createdAt: m.created_at.toISOString(),
      })),
    };
  }

  @Get('matches/:matchId')
  @RequirePermission('matches.read')
  async match(@Param('matchId') matchId: string) {
    const id = parseUuid(matchId, 'matchId');
    const m = await this.db.one<{
      id: string;
      status: string;
      map_version: string;
      address: string;
      completion_reason: string | null;
      created_at: Date;
      ended_at: Date | null;
    }>(
      `SELECT m.id, m.status, m.map_version, s.address, m.completion_reason, m.created_at, m.ended_at
         FROM matches m JOIN game_servers s ON s.id = m.game_server_id WHERE m.id = $1`,
      [id],
    );
    if (!m) throw NotFound('Match');
    const participants = await this.db.query<{
      user_id: string;
      display_name: string;
      placement: number | null;
      kills: number | null;
      survival_seconds: number | null;
    }>(
      `SELECT p.user_id, pr.display_name, p.placement, p.kills, p.survival_seconds
         FROM match_participants p JOIN player_profiles pr ON pr.user_id = p.user_id
        WHERE p.match_id = $1 ORDER BY p.placement NULLS LAST`,
      [id],
    );
    const evidence = await this.db.query<{
      outcome: string;
      reason: string | null;
      received_at: Date;
    }>(
      'SELECT outcome, reason, received_at FROM match_verification_records WHERE match_id = $1 ORDER BY id',
      [id],
    );
    return {
      matchId: m.id,
      status: m.status,
      mapVersion: m.map_version,
      server: m.address,
      completionReason: m.completion_reason,
      createdAt: m.created_at.toISOString(),
      endedAt: m.ended_at?.toISOString() ?? null,
      participants: participants.map((p) => ({
        playerId: p.user_id,
        displayName: p.display_name,
        placement: p.placement,
        kills: p.kills,
        survivalSeconds: p.survival_seconds,
      })),
      resultSubmissions: evidence.map((e) => ({
        outcome: e.outcome,
        reason: e.reason,
        receivedAt: e.received_at.toISOString(),
      })),
    };
  }

  // ---- Reports ----

  @Get('reports')
  @RequirePermission('reports.read')
  async reports(@Query() query: unknown) {
    const q = parse(listQuery, query);
    const rows = await this.db.query<{
      id: string;
      reported_user_id: string;
      reported_name: string;
      reporter_user_id: string | null;
      match_id: string | null;
      reason: string;
      details: string | null;
      status: string;
      created_at: Date;
    }>(
      `SELECT r.id, r.reported_user_id, p.display_name AS reported_name, r.reporter_user_id, r.match_id, r.reason, r.details,
              r.status, r.created_at
         FROM player_reports r JOIN player_profiles p ON p.user_id = r.reported_user_id
        WHERE ($1::text IS NULL OR r.status = $1) ORDER BY r.created_at LIMIT $2`,
      [q.status ?? null, q.limit],
    );
    return {
      items: rows.map((r) => ({
        reportId: r.id,
        reportedPlayerId: r.reported_user_id,
        reportedDisplayName: r.reported_name,
        reporterPlayerId: r.reporter_user_id,
        matchId: r.match_id,
        reason: r.reason,
        details: r.details,
        status: r.status,
        createdAt: r.created_at.toISOString(),
      })),
    };
  }

  @Patch('reports/:reportId')
  @RequirePermission('reports.resolve')
  async resolve(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('reportId') reportId: string,
    @Body() body: unknown,
  ) {
    const id = parseUuid(reportId, 'reportId');
    const r = parse(reportPatch, body);
    return this.db.tx(async (c) => {
      const { rows } = await c.query<{ status: string }>(
        'SELECT status FROM player_reports WHERE id = $1 FOR UPDATE',
        [id],
      );
      if (!rows[0]) throw NotFound('Report');
      await c.query('UPDATE player_reports SET status = $2 WHERE id = $1', [id, r.status]);
      await this.auth.audit(
        c,
        admin.actor,
        'report.status',
        'report',
        id,
        r.note,
        { from: rows[0].status, to: r.status },
        requestId,
      );
      return { reportId: id, status: r.status };
    });
  }
}
