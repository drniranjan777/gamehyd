import {
  Body,
  Controller,
  Get,
  HttpCode,
  Param,
  Post,
  Put,
  Query,
  UseGuards,
} from '@nestjs/common';
import { z } from 'zod';
import { Conflict, Invalid, NotFound, parse } from '../common/api-error';
import { Db } from '../database/database.module';
import { GameConfigService, gameConfigSchema } from '../store/game-config.service';
import { MetricsService } from '../telemetry/metrics.service';
import {
  AdminAuthService,
  AdminGuard,
  AdminPrincipal,
  CurrentAdmin,
  RequestId,
  RequirePermission,
} from './admin-auth';

const auditQuery = z
  .object({
    entityType: z
      .string()
      .regex(/^[a-z_]{2,50}$/)
      .optional(),
    entityId: z.string().min(1).max(100).optional(),
    actor: z.string().min(1).max(100).optional(),
    action: z
      .string()
      .regex(/^[a-z_.]{2,100}$/)
      .optional(),
    before: z.coerce.number().int().min(1).optional(),
    limit: z.coerce.number().int().min(1).max(200).default(100),
  })
  .strict();

const configBody = z
  .object({
    expectedVersion: z.number().int().min(1),
    config: gameConfigSchema,
    reason: z.string().min(3).max(300),
  })
  .strict();

const eventBody = z
  .object({
    key: z.string().regex(/^[a-z0-9_-]{3,60}$/),
    title: z.string().min(1).max(80),
    body: z.string().max(2000).default(''),
    startsAt: z.string().datetime(),
    endsAt: z.string().datetime(),
  })
  .strict();

@Controller('admin')
@UseGuards(AdminGuard)
export class AdminOpsController {
  constructor(
    private readonly db: Db,
    private readonly auth: AdminAuthService,
    private readonly gameConfig: GameConfigService,
    private readonly metrics: MetricsService,
  ) {}

  /** Beta KPIs and release gates (Phase 6). */
  @Get('metrics')
  @RequirePermission('metrics.read')
  async kpis(@Query() query: unknown) {
    const { days } = parse(
      z.object({ days: z.coerce.number().int().min(1).max(90).default(7) }).strict(),
      query,
    );
    return this.metrics.compute(days);
  }

  /** The append-only audit trail, newest first, paged by id. */
  @Get('audit')
  @RequirePermission('audit.read')
  async audit(@Query() query: unknown) {
    const q = parse(auditQuery, query);
    const rows = await this.db.query<{
      id: string;
      actor: string;
      action: string;
      entity_type: string;
      entity_id: string;
      reason: string | null;
      details: object | null;
      request_id: string | null;
      created_at: Date;
    }>(
      `SELECT id, actor, action, entity_type, entity_id, reason, details, request_id, created_at FROM admin_audit_logs
        WHERE ($1::text IS NULL OR entity_type = $1) AND ($2::text IS NULL OR entity_id = $2)
          AND ($3::text IS NULL OR actor = $3) AND ($4::text IS NULL OR action = $4) AND ($5::bigint IS NULL OR id < $5)
        ORDER BY id DESC LIMIT $6`,
      [
        q.entityType ?? null,
        q.entityId ?? null,
        q.actor ?? null,
        q.action ?? null,
        q.before ?? null,
        q.limit,
      ],
    );
    const items = rows.map((r) => ({
      id: Number(r.id),
      actor: r.actor,
      action: r.action,
      entityType: r.entity_type,
      entityId: r.entity_id,
      reason: r.reason,
      details: r.details,
      requestId: r.request_id,
      createdAt: r.created_at.toISOString(),
    }));
    return { items, nextBefore: items.length === q.limit ? items[items.length - 1]!.id : null };
  }

  // ---- Kill switches and live settings ----

  @Get('liveops/config')
  async config() {
    const history = await this.db.query<{
      version: number;
      config: object;
      reason: string;
      created_by: string;
      created_at: Date;
    }>(
      'SELECT version, config, reason, created_by, created_at FROM game_config_versions ORDER BY version DESC LIMIT 20',
    );
    return {
      current: { version: history[0]!.version, config: history[0]!.config },
      history: history.map((h) => ({
        version: h.version,
        config: h.config,
        reason: h.reason,
        createdBy: h.created_by,
        createdAt: h.created_at.toISOString(),
      })),
    };
  }

  /** Publishes a new config version. expectedVersion makes concurrent edits fail instead of silently overwriting. */
  @Put('liveops/config')
  @RequirePermission('liveops.write')
  async setConfig(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Body() body: unknown,
  ) {
    const b = parse(configBody, body);
    try {
      await this.db.tx(async (c) => {
        // Two concurrent publishers both insert expectedVersion + 1; the primary key lets only one win.
        const { rows } = await c.query<{ version: number }>(
          'SELECT max(version) AS version FROM game_config_versions',
        );
        if (rows[0]!.version !== b.expectedVersion)
          throw Conflict(
            'CONFIG_CHANGED',
            `Config is at version ${rows[0]!.version}; reload and try again`,
          );
        await c.query(
          'INSERT INTO game_config_versions (version, config, reason, created_by) VALUES ($1, $2, $3, $4)',
          [b.expectedVersion + 1, b.config, b.reason, admin.actor],
        );
        await this.auth.audit(
          c,
          admin.actor,
          'liveops.config',
          'game_config',
          String(b.expectedVersion + 1),
          b.reason,
          b.config,
          requestId,
        );
      });
    } catch (err) {
      if ((err as { code?: string }).code === '23505')
        throw Conflict('CONFIG_CHANGED', 'Config changed meanwhile; reload and try again');
      throw err;
    } finally {
      this.gameConfig.invalidate();
    }
    return this.config();
  }

  @Get('liveops/events')
  async events() {
    const rows = await this.db.query<{
      key: string;
      title: string;
      body: string;
      starts_at: Date;
      ends_at: Date;
      status: string;
      created_by: string;
    }>(
      'SELECT key, title, body, starts_at, ends_at, status, created_by FROM live_events ORDER BY starts_at DESC LIMIT 100',
    );
    return {
      items: rows.map((e) => ({
        key: e.key,
        title: e.title,
        body: e.body,
        startsAt: e.starts_at.toISOString(),
        endsAt: e.ends_at.toISOString(),
        status: e.status,
        createdBy: e.created_by,
      })),
    };
  }

  @Post('liveops/events')
  @HttpCode(201)
  @RequirePermission('liveops.write')
  async createEvent(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Body() body: unknown,
  ) {
    const e = parse(eventBody, body);
    if (new Date(e.endsAt) <= new Date(e.startsAt))
      throw Invalid('endsAt must be after startsAt', [{ path: 'endsAt', message: 'before start' }]);
    try {
      await this.db.tx(async (c) => {
        await c.query(
          'INSERT INTO live_events (key, title, body, starts_at, ends_at, created_by) VALUES ($1, $2, $3, $4, $5, $6)',
          [e.key, e.title, e.body, e.startsAt, e.endsAt, admin.actor],
        );
        await this.auth.audit(
          c,
          admin.actor,
          'liveops.event_create',
          'live_event',
          e.key,
          null,
          { title: e.title, startsAt: e.startsAt, endsAt: e.endsAt },
          requestId,
        );
      });
    } catch (err) {
      if ((err as { code?: string }).code === '23505')
        throw Conflict('EVENT_EXISTS', 'An event with this key exists');
      throw err;
    }
    return (await this.events()).items.find((i) => i.key === e.key);
  }

  @Post('liveops/events/:key/cancel')
  @HttpCode(200)
  @RequirePermission('liveops.write')
  async cancelEvent(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('key') key: string,
    @Body() body: unknown,
  ) {
    const { reason } = parse(z.object({ reason: z.string().min(3).max(500) }).strict(), body);
    await this.db.tx(async (c) => {
      const { rowCount } = await c.query(
        `UPDATE live_events SET status = 'CANCELLED' WHERE key = $1 AND status = 'SCHEDULED'`,
        [key],
      );
      if (!rowCount) throw NotFound('Scheduled event');
      await this.auth.audit(
        c,
        admin.actor,
        'liveops.event_cancel',
        'live_event',
        key,
        reason,
        null,
        requestId,
      );
    });
    return (await this.events()).items.find((i) => i.key === key);
  }
}
