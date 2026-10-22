import {
  Body,
  Controller,
  Delete,
  Get,
  Headers,
  HttpCode,
  Inject,
  Param,
  Post,
  Req,
  UseGuards,
} from '@nestjs/common';
import { z } from 'zod';
import { Conflict, NotFound, parse } from '../common/api-error';
import { AuthedRequest, AuthGuard, CurrentUser, RateLimit, RateLimitGuard } from '../common/guards';
import { parseUuid } from '../common/schemas';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';
import { levelFor } from '../player/progression';
import { GameConfigService } from './game-config.service';
import { StoreService } from './store.service';
import { WebhooksService } from './webhooks.service';

const productKey = z.string().regex(/^[a-z0-9_]{3,60}$/);
const idempotencyKey = z.string().regex(/^[A-Za-z0-9_:.-]{8,100}$/);
const proof = z.string().min(10).max(20_000);

const orderBody = z
  .object({ productKey, platform: z.enum(['IN_GAME', 'ANDROID', 'IOS', 'DEV']), idempotencyKey })
  .strict();
const verifyBody = z.object({ proof }).strict();
const restoreBody = z
  .object({
    platform: z.enum(['ANDROID', 'IOS', 'DEV']),
    sku: z.string().regex(/^[a-z0-9_.]{3,100}$/),
    proof,
  })
  .strict();

@Controller('store')
@UseGuards(AuthGuard, RateLimitGuard)
export class StoreController {
  constructor(private readonly store: StoreService) {}

  @Get('catalog')
  catalog(@CurrentUser() userId: string) {
    return this.store.catalog(userId);
  }

  @Post('orders')
  @HttpCode(201)
  @RateLimit({ limit: 30, windowSeconds: 60, key: 'user' })
  create(@CurrentUser() userId: string, @Body() body: unknown) {
    return this.store.createOrder(userId, parse(orderBody, body));
  }

  @Get('orders')
  list(@CurrentUser() userId: string) {
    return this.store.listOrders(userId);
  }

  @Get('orders/:orderId')
  get(@CurrentUser() userId: string, @Param('orderId') orderId: string) {
    return this.store.getOrder(userId, parseUuid(orderId, 'orderId'));
  }

  @Post('orders/:orderId/verify')
  @HttpCode(200)
  @RateLimit({ limit: 30, windowSeconds: 60, key: 'user' })
  verify(@CurrentUser() userId: string, @Param('orderId') orderId: string, @Body() body: unknown) {
    return this.store.verify(userId, parseUuid(orderId, 'orderId'), parse(verifyBody, body).proof);
  }

  @Post('restore')
  @HttpCode(200)
  @RateLimit({ limit: 20, windowSeconds: 60, key: 'user' })
  restore(@CurrentUser() userId: string, @Body() body: unknown) {
    const r = parse(restoreBody, body);
    return this.store.restore(userId, r.platform, r.sku, r.proof);
  }
}

/** Platform server-to-server notifications. Authenticated by the platform's own signature, not a player token. */
@Controller('webhooks')
@UseGuards(RateLimitGuard)
export class WebhooksController {
  constructor(private readonly hooks: WebhooksService) {}

  @Post('google-play')
  @HttpCode(200)
  @RateLimit({ limit: 600, windowSeconds: 60, key: 'ip' })
  async google(@Req() req: AuthedRequest, @Body() body: unknown) {
    return {
      outcome: await this.hooks.google(
        req.header('authorization'),
        req.rawBody ?? Buffer.alloc(0),
        body,
      ),
    };
  }

  @Post('app-store')
  @HttpCode(200)
  @RateLimit({ limit: 600, windowSeconds: 60, key: 'ip' })
  async apple(@Req() req: AuthedRequest, @Body() body: unknown) {
    return { outcome: await this.hooks.apple(req.rawBody ?? Buffer.alloc(0), body) };
  }

  @Post('dev')
  @HttpCode(200)
  async dev(
    @Req() req: AuthedRequest,
    @Headers('x-rwbr-dev-signature') signature: string | undefined,
    @Body() body: unknown,
  ) {
    return { outcome: await this.hooks.dev(signature, req.rawBody ?? Buffer.alloc(0), body) };
  }
}

/** Self-service account deletion with a grace period (BRD §13). Signing in during the grace period is allowed. */
@Controller('player/deletion')
@UseGuards(AuthGuard)
export class AccountDeletionController {
  constructor(
    private readonly db: Db,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {}

  @Get()
  async status(@CurrentUser() userId: string) {
    const row = await this.db.one<{ status: string; scheduled_for: Date | null }>(
      `SELECT u.status, d.scheduled_for FROM users u
         LEFT JOIN account_deletion_requests d ON d.user_id = u.id AND d.cancelled_at IS NULL AND d.completed_at IS NULL
        WHERE u.id = $1`,
      [userId],
    );
    return { status: row!.status, scheduledFor: row!.scheduled_for?.toISOString() ?? null };
  }

  @Post()
  @HttpCode(202)
  async request(@CurrentUser() userId: string) {
    await this.db.tx(async (c) => {
      const { rows } = await c.query<{ status: string }>(
        'SELECT status FROM users WHERE id = $1 FOR UPDATE',
        [userId],
      );
      if (rows[0]!.status === 'PENDING_DELETION') return;
      if (rows[0]!.status !== 'ACTIVE')
        throw Conflict('ACCOUNT_RESTRICTED', `Account is ${rows[0]!.status}`);
      await c.query(
        `INSERT INTO account_deletion_requests (user_id, scheduled_for) VALUES ($1, now() + make_interval(days => $2::int))
         ON CONFLICT (user_id) DO UPDATE SET requested_at = now(), scheduled_for = EXCLUDED.scheduled_for,
           cancelled_at = NULL, completed_at = NULL`,
        [userId, this.config.ACCOUNT_DELETION_GRACE_DAYS],
      );
      await c.query(`UPDATE users SET status = 'PENDING_DELETION' WHERE id = $1`, [userId]);
      await c.query('DELETE FROM matchmaking_queue WHERE user_id = $1', [userId]);
    });
    return this.status(userId);
  }

  @Delete()
  async cancel(@CurrentUser() userId: string) {
    await this.db.tx(async (c) => {
      const { rows } = await c.query<{ status: string }>(
        'SELECT status FROM users WHERE id = $1 FOR UPDATE',
        [userId],
      );
      if (rows[0]!.status !== 'PENDING_DELETION')
        throw Conflict('NO_DELETION_PENDING', 'No deletion is pending');
      await c.query(
        'UPDATE account_deletion_requests SET cancelled_at = now() WHERE user_id = $1',
        [userId],
      );
      await c.query(`UPDATE users SET status = 'ACTIVE' WHERE id = $1`, [userId]);
    });
    return this.status(userId);
  }
}

/** Unauthenticated read-only data for the website and the game's front end. */
@Controller()
@UseGuards(RateLimitGuard)
export class PublicController {
  constructor(
    private readonly db: Db,
    private readonly gameConfig: GameConfigService,
  ) {}

  @Get('players/:playerId/public')
  @RateLimit({ limit: 120, windowSeconds: 60, key: 'ip' })
  async player(@Param('playerId') playerId: string) {
    const p = await this.db.one<{
      user_id: string;
      display_name: string;
      xp: string;
      matches_played: number;
      wins: number;
      kills: number;
      created_at: Date;
    }>(
      `SELECT p.user_id, p.display_name, p.xp, p.matches_played, p.wins, p.kills, p.created_at
         FROM player_profiles p JOIN users u ON u.id = p.user_id
        WHERE p.user_id = $1 AND u.status IN ('ACTIVE', 'SUSPENDED')`,
      [parseUuid(playerId, 'playerId')],
    );
    if (!p) throw NotFound('Player');
    const recent = await this.db.query<{
      match_id: string;
      map_version: string;
      placement: number;
      kills: number;
      players: number;
      completed_at: Date;
    }>(
      `SELECT mp.match_id, m.map_version, mp.placement, mp.kills, mp.completed_at,
              (SELECT count(*)::int FROM match_participants x WHERE x.match_id = mp.match_id) AS players
         FROM match_participants mp JOIN matches m ON m.id = mp.match_id
        WHERE mp.user_id = $1 AND mp.completed_at IS NOT NULL AND m.status = 'COMPLETED'
        ORDER BY mp.completed_at DESC, mp.match_id DESC LIMIT 10`,
      [p.user_id],
    );
    return {
      playerId: p.user_id,
      displayName: p.display_name,
      level: levelFor(Number(p.xp)),
      matchesPlayed: p.matches_played,
      wins: p.wins,
      kills: p.kills,
      memberSince: p.created_at.toISOString(),
      recentMatches: recent.map((r) => ({
        matchId: r.match_id,
        mapVersion: r.map_version,
        placement: r.placement,
        players: r.players,
        kills: r.kills,
        completedAt: r.completed_at.toISOString(),
      })),
    };
  }

  @Get('events')
  @RateLimit({ limit: 120, windowSeconds: 60, key: 'ip' })
  async events() {
    const rows = await this.db.query<{
      key: string;
      title: string;
      body: string;
      starts_at: Date;
      ends_at: Date;
    }>(
      `SELECT key, title, body, starts_at, ends_at FROM live_events
        WHERE status = 'SCHEDULED' AND ends_at > now() ORDER BY starts_at LIMIT 50`,
    );
    return {
      items: rows.map((e) => ({
        key: e.key,
        title: e.title,
        body: e.body,
        startsAt: e.starts_at.toISOString(),
        endsAt: e.ends_at.toISOString(),
        live: e.starts_at.getTime() <= Date.now(),
      })),
    };
  }

  @Get('config')
  @RateLimit({ limit: 120, windowSeconds: 60, key: 'ip' })
  async config() {
    const { version, config } = await this.gameConfig.current();
    return {
      version,
      storeEnabled: config.storeEnabled,
      matchmakingEnabled: config.matchmakingEnabled,
      disabledProducts: config.disabledProducts,
      disabledWeapons: config.disabledWeapons,
      xpMultiplier: config.xpMultiplier,
    };
  }
}
