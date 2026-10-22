import { Body, Controller, HttpCode, Post, Req, UseGuards } from '@nestjs/common';
import { z } from 'zod';
import { parse } from '../common/api-error';
import { AuthedRequest, RateLimit, RateLimitGuard } from '../common/guards';
import { TokenService } from '../auth/token.service';
import { Db } from '../database/database.module';

const text = (max: number) => z.string().max(max);
const quality = z.enum(['LOW', 'MEDIUM', 'HIGH']);

/** Per-type payloads. Strict and small: no free-form data, nothing that identifies a person beyond the account. */
const eventSchema = z.discriminatedUnion('type', [
  z
    .object({
      type: z.literal('SESSION_START'),
      at: z.string().datetime(),
      data: z.object({ os: text(60).optional(), quality: quality.optional() }).strict(),
    })
    .strict(),
  z
    .object({
      type: z.literal('SESSION_END'),
      at: z.string().datetime(),
      data: z.object({ durationSeconds: z.number().int().min(0).max(86_400) }).strict(),
    })
    .strict(),
  z
    .object({
      type: z.literal('CRASH'),
      at: z.string().datetime(),
      data: z
        .object({ message: text(300), where: text(200).optional(), stack: text(2000).optional() })
        .strict(),
    })
    .strict(),
  z
    .object({
      type: z.literal('FPS_SAMPLE'),
      at: z.string().datetime(),
      data: z
        .object({
          avgFps: z.number().min(0).max(1000),
          p5Fps: z.number().min(0).max(1000),
          seconds: z.number().int().min(1).max(3600),
          scene: z.enum(['MENU', 'PRACTICE', 'ONLINE']).optional(),
          quality: quality.optional(),
        })
        .strict(),
    })
    .strict(),
  z
    .object({
      type: z.literal('DISCONNECT'),
      at: z.string().datetime(),
      data: z
        .object({
          reason: text(60),
          matchId: z.string().uuid().optional(),
          reconnected: z.boolean().optional(),
        })
        .strict(),
    })
    .strict(),
]);

const batchSchema = z
  .object({
    sessionId: z.string().uuid(),
    appVersion: z.string().regex(/^\d+\.\d+\.\d+$/),
    platform: z.enum(['ANDROID', 'IOS', 'WINDOWS', 'MACOS', 'LINUX', 'EDITOR']),
    deviceModel: z.string().min(1).max(100),
    events: z.array(eventSchema).min(1).max(100),
  })
  .strict();

const MAX_AGE_MS = 7 * 86_400_000;

/**
 * Client telemetry (Phase 6 beta KPIs). Accepted with or without sign-in, so crashes before sign-in still count;
 * a valid access token attaches the account. Events older than 7 days are dropped; future timestamps are clamped.
 */
@Controller('telemetry')
@UseGuards(RateLimitGuard)
export class TelemetryController {
  constructor(
    private readonly db: Db,
    private readonly tokens: TokenService,
  ) {}

  @Post('events')
  @HttpCode(202)
  @RateLimit({ limit: 60, windowSeconds: 60, key: 'ip' })
  async ingest(@Req() req: AuthedRequest, @Body() body: unknown) {
    const b = parse(batchSchema, body);
    let userId: string | null = null;
    const bearer = /^Bearer ([A-Za-z0-9._-]+)$/.exec(req.header('authorization') ?? '');
    if (bearer) userId = await this.tokens.verifyAccess(bearer[1]!).catch(() => null);

    const now = Date.now();
    const rows = b.events
      .map((e) => ({ ...e, t: Math.min(Date.parse(e.at), now) }))
      .filter((e) => now - e.t <= MAX_AGE_MS);
    if (rows.length > 0) {
      await this.db.query(
        `INSERT INTO telemetry_events (user_id, session_id, event_type, app_version, platform, device_model, data, client_time)
         SELECT $1, $2, e.type, $3, $4, $5, e.data, e.at
           FROM jsonb_to_recordset($6::jsonb) AS e(type text, data jsonb, at timestamptz)`,
        [
          userId,
          b.sessionId,
          b.appVersion,
          b.platform,
          b.deviceModel,
          JSON.stringify(
            rows.map((e) => ({ type: e.type, data: e.data, at: new Date(e.t).toISOString() })),
          ),
        ],
      );
    }
    return { accepted: rows.length, dropped: b.events.length - rows.length };
  }
}
