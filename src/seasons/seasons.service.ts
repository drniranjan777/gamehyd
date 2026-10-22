import { Injectable } from '@nestjs/common';
import { PoolClient } from 'pg';
import { Conflict, Forbidden, NotFound } from '../common/api-error';
import { Db } from '../database/database.module';
import { credit } from '../matchmaking/results.service';

export type Track = 'FREE' | 'PREMIUM';

interface SeasonRow {
  id: string;
  key: string;
  title: string;
  starts_at: Date;
  ends_at: Date;
  xp_per_tier: number;
}

interface RewardRow {
  tier: number;
  track: Track;
  cosmetic_id: string | null;
  cosmetic_key: string | null;
  currency_code: 'COIN' | 'GEM' | null;
  amount: string | null;
}

const CURRENT = `SELECT id, key, title, starts_at, ends_at, xp_per_tier FROM seasons
                  WHERE status = 'PUBLISHED' AND starts_at <= now() AND ends_at > now()`;

export const rewardView = (r: RewardRow) =>
  r.cosmetic_key
    ? { type: 'COSMETIC' as const, cosmeticKey: r.cosmetic_key }
    : { type: 'CURRENCY' as const, currency: r.currency_code!, amount: Number(r.amount) };

/** Season XP is the match XP earned while a season runs; called inside the result transaction (exactly once). */
export async function addSeasonXp(c: PoolClient, userId: string, xp: number): Promise<void> {
  if (xp <= 0) return;
  await c.query(
    `INSERT INTO season_progress (season_id, user_id, xp)
     SELECT id, $1, $2 FROM seasons WHERE status = 'PUBLISHED' AND starts_at <= now() AND ends_at > now()
     ON CONFLICT (season_id, user_id) DO UPDATE SET xp = season_progress.xp + EXCLUDED.xp`,
    [userId, xp],
  );
}

/**
 * The season pass (BRD §9 live operations, PHASE-PLAN Phase 7). Players earn season XP from matches; every
 * `xp_per_tier` unlocks a tier with a free reward and, with the premium pass (a store product), a premium reward.
 * Rewards are claimed explicitly; the season_claims primary key makes each one pay out exactly once.
 */
@Injectable()
export class SeasonsService {
  constructor(private readonly db: Db) {}

  async current(userId: string) {
    const s = await this.db.one<SeasonRow>(CURRENT);
    if (!s) return { season: null };
    const [progress, rewards, claims, product] = await Promise.all([
      this.db.one<{ xp: string; premium: boolean }>(
        'SELECT xp, premium FROM season_progress WHERE season_id = $1 AND user_id = $2',
        [s.id, userId],
      ),
      this.rewards(this.db, s.id),
      this.db.query<{ tier: number; track: Track }>(
        'SELECT tier, track FROM season_claims WHERE season_id = $1 AND user_id = $2',
        [s.id, userId],
      ),
      this.db.one<{ key: string }>(
        `SELECT p.key FROM store_products p JOIN store_product_grants g ON g.product_id = p.id
          WHERE g.season_id = $1 AND p.status = 'PUBLISHED' ORDER BY p.published_at DESC LIMIT 1`,
        [s.id],
      ),
    ]);
    const xp = Number(progress?.xp ?? 0);
    const tier = Math.floor(xp / s.xp_per_tier);
    const premium = progress?.premium ?? false;
    const claimed = new Set(claims.map((c) => `${c.tier}:${c.track}`));
    return {
      season: {
        key: s.key,
        title: s.title,
        startsAt: s.starts_at.toISOString(),
        endsAt: s.ends_at.toISOString(),
        xpPerTier: s.xp_per_tier,
        maxTier: Math.max(0, ...rewards.map((r) => r.tier)),
        premiumProductKey: product?.key ?? null,
      },
      progress: { xp, tier, premium, xpToNextTier: s.xp_per_tier - (xp % s.xp_per_tier) },
      rewards: rewards.map((r) => ({
        tier: r.tier,
        track: r.track,
        reward: rewardView(r),
        unlocked: r.tier <= tier && (r.track === 'FREE' || premium),
        claimed: claimed.has(`${r.tier}:${r.track}`),
      })),
    };
  }

  async claim(userId: string, tier: number, track: Track) {
    const s = await this.db.one<SeasonRow>(CURRENT);
    if (!s) throw NotFound('Active season');
    await this.db.tx(async (c) => {
      await c.query(
        `INSERT INTO season_progress (season_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING`,
        [s.id, userId],
      );
      const { rows } = await c.query<{ xp: string; premium: boolean }>(
        'SELECT xp, premium FROM season_progress WHERE season_id = $1 AND user_id = $2 FOR UPDATE',
        [s.id, userId],
      );
      const progress = rows[0]!;
      const reward = (await this.rewards(c, s.id)).find(
        (r) => r.tier === tier && r.track === track,
      );
      if (!reward) throw NotFound('Reward');
      if (Math.floor(Number(progress.xp) / s.xp_per_tier) < tier)
        throw Forbidden('TIER_LOCKED', 'Play more matches to reach this tier');
      if (track === 'PREMIUM' && !progress.premium)
        throw Forbidden('PREMIUM_REQUIRED', 'This reward needs the season pass');
      const inserted = await c.query(
        `INSERT INTO season_claims (season_id, user_id, tier, track) VALUES ($1, $2, $3, $4)
         ON CONFLICT DO NOTHING RETURNING tier`,
        [s.id, userId, tier, track],
      );
      if (!inserted.rowCount) throw Conflict('ALREADY_CLAIMED', 'You already claimed this reward');
      const reference = `season:${s.id}:${tier}:${track}`;
      if (reward.cosmetic_id) {
        await c.query(
          `INSERT INTO player_inventory (user_id, cosmetic_id, source, source_reference)
           VALUES ($1, $2, 'SEASON_REWARD', $3) ON CONFLICT (user_id, cosmetic_id) DO NOTHING`,
          [userId, reward.cosmetic_id, reference],
        );
      } else {
        await credit(
          c,
          userId,
          reward.currency_code!,
          Number(reward.amount),
          'SEASON_REWARD',
          reference,
          reference,
        );
      }
    });
    return this.current(userId);
  }

  private async rewards(q: Db | PoolClient, seasonId: string): Promise<RewardRow[]> {
    const sql = `SELECT r.tier, r.track, r.cosmetic_id, c.key AS cosmetic_key, r.currency_code, r.amount
                   FROM season_rewards r LEFT JOIN cosmetics c ON c.id = r.cosmetic_id
                  WHERE r.season_id = $1 ORDER BY r.tier, r.track`;
    return q instanceof Db
      ? q.query<RewardRow>(sql, [seasonId])
      : (await q.query<RewardRow>(sql, [seasonId])).rows;
  }
}
