import { Injectable } from '@nestjs/common';
import { z } from 'zod';
import { ApiError } from '../common/api-error';
import { Db } from '../database/database.module';

/** Live settings and kill switches (BRD §9 "Live operations"). Unknown keys are kept but ignored. */
export const gameConfigSchema = z
  .object({
    storeEnabled: z.boolean(),
    matchmakingEnabled: z.boolean(),
    disabledProducts: z.array(z.string().regex(/^[a-z0-9_]{3,60}$/)).max(200),
    disabledWeapons: z.array(z.string().regex(/^[a-z0-9_]{2,40}$/)).max(50),
  })
  .passthrough();

export type GameConfig = z.infer<typeof gameConfigSchema>;

const CACHE_MS = 5_000;

/** Reads the highest config version, cached briefly so hot paths (queue, store) don't hit the DB each time. */
@Injectable()
export class GameConfigService {
  private cached?: { at: number; version: number; config: GameConfig };

  constructor(private readonly db: Db) {}

  async current(): Promise<{ version: number; config: GameConfig }> {
    if (this.cached && Date.now() - this.cached.at < CACHE_MS) return this.cached;
    const row = await this.db.one<{ version: number; config: unknown }>(
      'SELECT version, config FROM game_config_versions ORDER BY version DESC LIMIT 1',
    );
    if (!row) throw new Error('game_config_versions is empty; migration 0007 seeds version 1');
    this.cached = {
      at: Date.now(),
      version: row.version,
      config: gameConfigSchema.parse(row.config),
    };
    return this.cached;
  }

  /** Drops the cache; called after an admin publishes a new version. */
  invalidate(): void {
    this.cached = undefined;
  }

  async assertStoreOpen(productKey?: string): Promise<void> {
    const { config } = await this.current();
    if (!config.storeEnabled)
      throw new ApiError(503, 'STORE_DISABLED', 'The store is temporarily closed');
    if (productKey && config.disabledProducts.includes(productKey))
      throw new ApiError(409, 'PRODUCT_UNAVAILABLE', 'This product is temporarily unavailable');
  }

  async assertMatchmakingOpen(): Promise<void> {
    const { config } = await this.current();
    if (!config.matchmakingEnabled)
      throw new ApiError(503, 'MATCHMAKING_DISABLED', 'Matchmaking is temporarily paused');
  }
}
