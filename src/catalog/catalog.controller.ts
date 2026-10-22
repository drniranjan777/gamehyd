import { Body, Controller, Get, HttpCode, Param, Post, Put, UseGuards } from '@nestjs/common';
import { z } from 'zod';
import { Forbidden, Invalid, NotFound, parse } from '../common/api-error';
import { AuthGuard, CurrentUser, RateLimit, RateLimitGuard } from '../common/guards';
import { parseUuid } from '../common/schemas';
import { Db } from '../database/database.module';

const SLOTS = ['WEAPON_SKIN', 'OUTFIT', 'EMOTE', 'PARACHUTE', 'BANNER'] as const;

@Controller('maps')
export class MapsController {
  constructor(private readonly db: Db) {}

  @Get()
  async list() {
    const rows = await this.db.query<{
      map_key: string;
      display_name: string;
      version: string;
      published_at: Date;
    }>(
      `SELECT v.map_key, m.display_name, v.version, v.published_at
         FROM map_versions v JOIN maps m ON m.key = v.map_key
        WHERE v.status = 'PUBLISHED' ORDER BY v.map_key, v.published_at DESC, v.version`,
    );
    return {
      items: rows.map((r) => ({
        mapKey: r.map_key,
        displayName: r.display_name,
        version: r.version,
        publishedAt: r.published_at.toISOString(),
      })),
    };
  }

  @Get(':mapKey/manifest')
  async manifest(@Param('mapKey') mapKey: string) {
    if (!/^[a-z0-9-]{3,40}$/.test(mapKey)) throw NotFound('Map');
    const row = await this.db.one<{ map_key: string; version: string; manifest: object }>(
      `SELECT map_key, version, manifest FROM map_versions
        WHERE map_key = $1 AND status = 'PUBLISHED' ORDER BY published_at DESC, version DESC LIMIT 1`,
      [mapKey],
    );
    if (!row) throw NotFound('Map');
    return { mapKey: row.map_key, version: row.version, manifest: row.manifest };
  }
}

@Controller('inventory')
@UseGuards(AuthGuard)
export class InventoryController {
  constructor(private readonly db: Db) {}

  @Get()
  async get(@CurrentUser() userId: string) {
    const items = await this.db.query<{
      key: string;
      kind: string;
      display_name: string;
      rarity: string;
      acquired_at: Date;
    }>(
      `SELECT c.key, c.kind, c.display_name, c.rarity, i.acquired_at
         FROM player_inventory i JOIN cosmetics c ON c.id = i.cosmetic_id
        WHERE i.user_id = $1 ORDER BY i.acquired_at, c.key`,
      [userId],
    );
    const loadout = await this.db.query<{ slot: string; key: string }>(
      `SELECT l.slot, c.key FROM loadout_slots l JOIN cosmetics c ON c.id = l.cosmetic_id WHERE l.user_id = $1`,
      [userId],
    );
    const wallets = await this.db.query<{ currency_code: 'COIN' | 'GEM'; balance: string }>(
      'SELECT currency_code, balance FROM wallets WHERE user_id = $1',
      [userId],
    );
    const balance = (code: string) =>
      Number(wallets.find((w) => w.currency_code === code)?.balance ?? 0);
    return {
      items: items.map((i) => ({
        cosmeticKey: i.key,
        kind: i.kind,
        displayName: i.display_name,
        rarity: i.rarity,
        acquiredAt: i.acquired_at.toISOString(),
      })),
      loadout: Object.fromEntries(loadout.map((l) => [l.slot, l.key])),
      wallets: { COIN: balance('COIN'), GEM: balance('GEM') },
    };
  }

  @Put('loadout/:slot')
  async equip(@CurrentUser() userId: string, @Param('slot') slot: string, @Body() body: unknown) {
    if (!(SLOTS as readonly string[]).includes(slot))
      throw Invalid('Unknown loadout slot', [{ path: 'slot', message: 'invalid slot' }]);
    const { cosmeticKey } = parse(
      z.object({ cosmeticKey: z.string().regex(/^[a-z0-9_]{3,60}$/) }).strict(),
      body,
    );
    const owned = await this.db.one<{ id: string; kind: string }>(
      `SELECT c.id, c.kind FROM player_inventory i JOIN cosmetics c ON c.id = i.cosmetic_id
        WHERE i.user_id = $1 AND c.key = $2`,
      [userId, cosmeticKey],
    );
    if (!owned) throw Forbidden('NOT_OWNED', 'You do not own that cosmetic');
    if (owned.kind !== slot)
      throw Invalid('Cosmetic does not fit this slot', [
        { path: 'cosmeticKey', message: `is a ${owned.kind}` },
      ]);
    await this.db.query(
      `INSERT INTO loadout_slots (user_id, slot, cosmetic_id) VALUES ($1, $2, $3)
       ON CONFLICT (user_id, slot) DO UPDATE SET cosmetic_id = EXCLUDED.cosmetic_id`,
      [userId, slot, owned.id],
    );
    return this.get(userId);
  }
}

const reportBody = z
  .object({
    reportedPlayerId: z.string().uuid(),
    matchId: z.string().uuid().optional(),
    reason: z.enum(['CHEATING', 'HARASSMENT', 'INAPPROPRIATE_NAME', 'GRIEFING', 'OTHER']),
    details: z.string().max(1000).optional(),
  })
  .strict();

interface ReportRow {
  id: string;
  reported_user_id: string;
  match_id: string | null;
  reason: string;
  status: string;
  created_at: Date;
}

const reportView = (r: ReportRow) => ({
  reportId: r.id,
  reportedPlayerId: r.reported_user_id,
  matchId: r.match_id,
  reason: r.reason,
  status: r.status,
  createdAt: r.created_at.toISOString(),
});

@Controller('reports')
@UseGuards(AuthGuard, RateLimitGuard)
export class ReportsController {
  constructor(private readonly db: Db) {}

  @Post()
  @HttpCode(201)
  @RateLimit({ limit: 10, windowSeconds: 3600, key: 'user' })
  async create(@CurrentUser() userId: string, @Body() body: unknown) {
    const r = parse(reportBody, body);
    if (r.reportedPlayerId === userId)
      throw Invalid('You cannot report yourself', [
        { path: 'reportedPlayerId', message: 'is you' },
      ]);
    const target = await this.db.one('SELECT 1 FROM users WHERE id = $1', [r.reportedPlayerId]);
    if (!target) throw NotFound('Player');
    if (r.matchId && !(await this.db.one('SELECT 1 FROM matches WHERE id = $1', [r.matchId])))
      throw NotFound('Match');
    const row = await this.db.one<ReportRow>(
      `INSERT INTO player_reports (reporter_user_id, reported_user_id, match_id, reason, details)
       VALUES ($1, $2, $3, $4, $5)
       RETURNING id, reported_user_id, match_id, reason, status, created_at`,
      [userId, r.reportedPlayerId, r.matchId ?? null, r.reason, r.details ?? null],
    );
    return reportView(row!);
  }

  @Get(':reportId')
  async get(@CurrentUser() userId: string, @Param('reportId') reportId: string) {
    const row = await this.db.one<ReportRow>(
      `SELECT id, reported_user_id, match_id, reason, status, created_at
         FROM player_reports WHERE id = $1 AND reporter_user_id = $2`,
      [parseUuid(reportId, 'reportId'), userId],
    );
    if (!row) throw NotFound('Report');
    return reportView(row);
  }
}
