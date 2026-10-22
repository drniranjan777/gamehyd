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
import { AuthGuard, CurrentUser, RateLimit, RateLimitGuard } from '../common/guards';
import { parseUuid } from '../common/schemas';
import { Db } from '../database/database.module';
import {
  AdminAuthService,
  AdminGuard,
  AdminPrincipal,
  CurrentAdmin,
  RequestId,
  RequirePermission,
} from '../admin/admin-auth';
import { rewardView, SeasonsService } from '../seasons/seasons.service';
import { EconomyService } from './economy.service';
import { OpsService } from './ops.service';
import { ReleasesService } from './releases.service';

// ---- Player: season pass ----

@Controller('seasons')
@UseGuards(AuthGuard, RateLimitGuard)
export class SeasonsController {
  constructor(private readonly seasons: SeasonsService) {}

  @Get('current')
  current(@CurrentUser() userId: string) {
    return this.seasons.current(userId);
  }

  @Post('current/claim')
  @HttpCode(200)
  @RateLimit({ limit: 60, windowSeconds: 60, key: 'user' })
  claim(@CurrentUser() userId: string, @Body() body: unknown) {
    const c = parse(
      z
        .object({ tier: z.number().int().min(1).max(100), track: z.enum(['FREE', 'PREMIUM']) })
        .strict(),
      body,
    );
    return this.seasons.claim(userId, c.tier, c.track);
  }
}

// ---- Admin: operations, releases, economy, seasons ----

const reward = z.discriminatedUnion('type', [
  z
    .object({ type: z.literal('COSMETIC'), cosmeticKey: z.string().regex(/^[a-z0-9_]{3,60}$/) })
    .strict(),
  z
    .object({
      type: z.literal('CURRENCY'),
      currency: z.enum(['COIN', 'GEM']),
      amount: z.number().int().min(1).max(100_000),
    })
    .strict(),
]);
const seasonBody = z
  .object({
    key: z.string().regex(/^[a-z0-9_-]{3,40}$/),
    title: z.string().min(1).max(80),
    startsAt: z.string().datetime(),
    endsAt: z.string().datetime(),
    xpPerTier: z.number().int().min(100).max(100_000),
    rewards: z
      .array(
        z
          .object({
            tier: z.number().int().min(1).max(100),
            track: z.enum(['FREE', 'PREMIUM']),
            reward,
          })
          .strict(),
      )
      .min(1)
      .max(200),
  })
  .strict();

const releaseBody = z
  .object({
    platform: z.enum(['ANDROID', 'IOS']),
    appVersion: z.string().regex(/^\d+\.\d+\.\d+$/),
    rolloutPercent: z.number().int().min(1).max(100),
    regions: z
      .array(z.string().regex(/^[A-Z]{2}(-[A-Z]{2,10})?$/))
      .max(20)
      .default([]),
    minCrashFree: z.number().min(0.9).max(1).default(0.995),
    minSessions: z.number().int().min(10).max(1_000_000).default(500),
  })
  .strict();

@Controller('admin')
@UseGuards(AdminGuard)
export class AdminLiveOpsController {
  constructor(
    private readonly db: Db,
    private readonly auth: AdminAuthService,
    private readonly ops: OpsService,
    private readonly releases: ReleasesService,
    private readonly economy: EconomyService,
  ) {}

  @Get('ops')
  @RequirePermission('ops.read')
  dashboard(@Query() query: unknown) {
    const { hours } = parse(
      z.object({ hours: z.coerce.number().int().min(1).max(720).default(24) }).strict(),
      query,
    );
    return this.ops.compute(hours);
  }

  @Get('economy')
  @RequirePermission('economy.read')
  economyReport(@Query() query: unknown) {
    const { days } = parse(
      z.object({ days: z.coerce.number().int().min(1).max(365).default(30) }).strict(),
      query,
    );
    return this.economy.report(days);
  }

  // ---- Releases ----

  @Get('releases')
  @RequirePermission('ops.read')
  listReleases() {
    return this.releases.list();
  }

  @Post('releases')
  @HttpCode(201)
  @RequirePermission('releases.write')
  async createRelease(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Body() body: unknown,
  ) {
    const r = parse(releaseBody, body);
    const id = await this.releases.create(r, admin.actor, (c, newId, details) =>
      this.auth.audit(c, admin.actor, 'release.create', 'release', newId, null, details, requestId),
    );
    return (await this.releases.list()).items.find((i) => i.id === id);
  }

  @Patch('releases/:releaseId')
  @RequirePermission('releases.write')
  async updateRelease(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('releaseId') releaseId: string,
    @Body() body: unknown,
  ) {
    const id = parseUuid(releaseId, 'releaseId');
    const change = parse(
      z
        .object({
          rolloutPercent: z.number().int().min(1).max(100).optional(),
          status: z.enum(['ROLLING_OUT', 'COMPLETE', 'ROLLED_BACK']).optional(),
          reason: z.string().min(3).max(500),
        })
        .strict(),
      body,
    );
    await this.releases.update(id, change, (c, before) =>
      this.auth.audit(
        c,
        admin.actor,
        'release.update',
        'release',
        id,
        change.reason,
        {
          from: { status: before.status, rolloutPercent: before.rollout_percent },
          to: {
            status: change.status ?? before.status,
            rolloutPercent: change.rolloutPercent ?? before.rollout_percent,
          },
        },
        requestId,
      ),
    );
    return (await this.releases.list()).items.find((i) => i.id === id);
  }

  // ---- Seasons ----

  @Get('seasons')
  @RequirePermission('catalog.read')
  async listSeasons() {
    const rows = await this.db.query<{
      id: string;
      key: string;
      title: string;
      starts_at: Date;
      ends_at: Date;
      xp_per_tier: number;
      status: string;
      created_by: string;
    }>(
      'SELECT id, key, title, starts_at, ends_at, xp_per_tier, status, created_by FROM seasons ORDER BY starts_at DESC LIMIT 50',
    );
    const items = [];
    for (const s of rows) {
      const rewards = await this.db.query<{
        tier: number;
        track: 'FREE' | 'PREMIUM';
        cosmetic_id: string | null;
        cosmetic_key: string | null;
        currency_code: 'COIN' | 'GEM' | null;
        amount: string | null;
      }>(
        `SELECT r.tier, r.track, r.cosmetic_id, c.key AS cosmetic_key, r.currency_code, r.amount
           FROM season_rewards r LEFT JOIN cosmetics c ON c.id = r.cosmetic_id WHERE r.season_id = $1 ORDER BY r.tier, r.track`,
        [s.id],
      );
      items.push({
        key: s.key,
        title: s.title,
        startsAt: s.starts_at.toISOString(),
        endsAt: s.ends_at.toISOString(),
        xpPerTier: s.xp_per_tier,
        status: s.status,
        createdBy: s.created_by,
        rewards: rewards.map((r) => ({ tier: r.tier, track: r.track, reward: rewardView(r) })),
      });
    }
    return { items };
  }

  /** Creates a DRAFT season with its reward track (replacing the rewards if the draft exists). */
  @Post('seasons')
  @HttpCode(201)
  @RequirePermission('liveops.write')
  async saveSeason(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Body() body: unknown,
  ) {
    const s = parse(seasonBody, body);
    if (new Date(s.endsAt) <= new Date(s.startsAt))
      throw Invalid('endsAt must be after startsAt', [{ path: 'endsAt', message: 'before start' }]);
    await this.db
      .tx(async (c) => {
        const existing = await c.query<{ id: string; status: string }>(
          'SELECT id, status FROM seasons WHERE key = $1 FOR UPDATE',
          [s.key],
        );
        if (existing.rows[0]?.status === 'PUBLISHED')
          throw Conflict('SEASON_PUBLISHED', 'Published seasons are immutable');
        const { rows } = existing.rows[0]
          ? await c.query<{ id: string }>(
              'UPDATE seasons SET title = $2, starts_at = $3, ends_at = $4, xp_per_tier = $5 WHERE id = $1 RETURNING id',
              [existing.rows[0].id, s.title, s.startsAt, s.endsAt, s.xpPerTier],
            )
          : await c.query<{ id: string }>(
              'INSERT INTO seasons (key, title, starts_at, ends_at, xp_per_tier, created_by) VALUES ($1, $2, $3, $4, $5, $6) RETURNING id',
              [s.key, s.title, s.startsAt, s.endsAt, s.xpPerTier, admin.actor],
            );
        const seasonId = rows[0]!.id;
        await c.query('DELETE FROM season_rewards WHERE season_id = $1', [seasonId]);
        for (const r of s.rewards) {
          if (r.reward.type === 'COSMETIC') {
            const cos = await c.query<{ id: string }>('SELECT id FROM cosmetics WHERE key = $1', [
              r.reward.cosmeticKey,
            ]);
            if (!cos.rows[0])
              throw Invalid('Unknown cosmetic', [
                { path: 'rewards', message: `${r.reward.cosmeticKey} does not exist` },
              ]);
            await c.query(
              'INSERT INTO season_rewards (season_id, tier, track, cosmetic_id) VALUES ($1, $2, $3, $4)',
              [seasonId, r.tier, r.track, cos.rows[0].id],
            );
          } else {
            await c.query(
              'INSERT INTO season_rewards (season_id, tier, track, currency_code, amount) VALUES ($1, $2, $3, $4, $5)',
              [seasonId, r.tier, r.track, r.reward.currency, r.reward.amount],
            );
          }
        }
        await this.auth.audit(
          c,
          admin.actor,
          'season.save',
          'season',
          s.key,
          null,
          { rewards: s.rewards.length, startsAt: s.startsAt, endsAt: s.endsAt },
          requestId,
        );
      })
      .catch((err: { code?: string }) => {
        if (err.code === '23505')
          throw Conflict('DUPLICATE_REWARD', 'Each tier and track can have one reward');
        throw err;
      });
    return (await this.listSeasons()).items.find((i) => i.key === s.key);
  }

  @Post('seasons/:key/publish')
  @HttpCode(200)
  @RequirePermission('liveops.write')
  async publishSeason(
    @CurrentAdmin() admin: AdminPrincipal,
    @RequestId() requestId: string,
    @Param('key') key: string,
  ) {
    await this.db
      .tx(async (c) => {
        const { rows } = await c.query<{ id: string; status: string }>(
          'SELECT id, status FROM seasons WHERE key = $1 FOR UPDATE',
          [key],
        );
        if (!rows[0]) throw NotFound('Season');
        if (rows[0].status === 'PUBLISHED') throw Conflict('SEASON_PUBLISHED', 'Already published');
        await c.query(`UPDATE seasons SET status = 'PUBLISHED' WHERE id = $1`, [rows[0].id]);
        await this.auth.audit(
          c,
          admin.actor,
          'season.publish',
          'season',
          key,
          null,
          null,
          requestId,
        );
      })
      .catch((err: { code?: string }) => {
        if (err.code === '23P01')
          throw Conflict('SEASON_OVERLAP', 'Another published season covers these dates');
        throw err;
      });
    return (await this.listSeasons()).items.find((i) => i.key === key);
  }
}
