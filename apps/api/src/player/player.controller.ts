import { Body, Controller, Get, Patch, Query, UseGuards } from '@nestjs/common';
import { z } from 'zod';
import { Invalid, NotFound, parse } from '../common/api-error';
import { AuthGuard, CurrentUser } from '../common/guards';
import { displayName } from '../common/schemas';
import { Db } from '../database/database.module';
import { levelFor, xpForNextLevel } from './progression';

interface ProfileRow {
  user_id: string;
  display_name: string;
  xp: string;
  matches_played: number;
  wins: number;
  kills: number;
  created_at: Date;
}

const historyQuery = z
  .object({
    cursor: z.string().max(200).optional(),
    limit: z.coerce.number().int().min(1).max(50).default(20),
  })
  .strict();

@Controller('player')
@UseGuards(AuthGuard)
export class PlayerController {
  constructor(private readonly db: Db) {}

  private async row(userId: string): Promise<ProfileRow> {
    const p = await this.db.one<ProfileRow>(
      'SELECT user_id, display_name, xp, matches_played, wins, kills, created_at FROM player_profiles WHERE user_id = $1',
      [userId],
    );
    if (!p) throw NotFound('Profile');
    return p;
  }

  private static profile(p: ProfileRow) {
    const xp = Number(p.xp);
    return {
      playerId: p.user_id,
      displayName: p.display_name,
      level: levelFor(xp),
      xp,
      xpForNextLevel: xpForNextLevel(xp),
      createdAt: p.created_at.toISOString(),
    };
  }

  @Get('profile')
  async getProfile(@CurrentUser() userId: string) {
    return PlayerController.profile(await this.row(userId));
  }

  @Patch('profile')
  async updateProfile(@CurrentUser() userId: string, @Body() body: unknown) {
    const { displayName: name } = parse(z.object({ displayName }).strict(), body);
    await this.db.query('UPDATE player_profiles SET display_name = $2 WHERE user_id = $1', [
      userId,
      name,
    ]);
    return PlayerController.profile(await this.row(userId));
  }

  @Get('stats')
  async stats(@CurrentUser() userId: string) {
    const p = await this.row(userId);
    const xp = Number(p.xp);
    return {
      matchesPlayed: p.matches_played,
      wins: p.wins,
      kills: p.kills,
      winRate: p.matches_played === 0 ? 0 : p.wins / p.matches_played,
      level: levelFor(xp),
      xp,
    };
  }

  /** Newest first. The cursor is opaque to clients: the last row's (completed_at, match_id). */
  @Get('matches')
  async matches(@CurrentUser() userId: string, @Query() query: unknown) {
    const { cursor, limit } = parse(historyQuery, query);
    let after: [string, string] | null = null;
    if (cursor) {
      try {
        const decoded = JSON.parse(Buffer.from(cursor, 'base64url').toString('utf8'));
        if (
          !Array.isArray(decoded) ||
          decoded.length !== 2 ||
          !decoded.every((x) => typeof x === 'string')
        )
          throw 0;
        after = decoded as [string, string];
      } catch {
        throw Invalid('cursor is not valid', [{ path: 'cursor', message: 'invalid cursor' }]);
      }
    }

    const rows = await this.db
      .query<{
        match_id: string;
        map_version: string;
        mode: string;
        completed_at: Date;
        completed_at_text: string;
        placement: number;
        players: number;
        kills: number;
        survival_seconds: number;
        xp_awarded: number;
        coins_awarded: number;
      }>(
        `SELECT p.match_id, m.map_version, m.mode, p.completed_at, p.completed_at::text AS completed_at_text,
              p.placement, p.kills, p.survival_seconds, p.xp_awarded, p.coins_awarded,
              (SELECT count(*)::int FROM match_participants x WHERE x.match_id = p.match_id AND x.placement IS NOT NULL) AS players
         FROM match_participants p JOIN matches m ON m.id = p.match_id
        WHERE p.user_id = $1 AND p.completed_at IS NOT NULL
          AND ($2::timestamptz IS NULL OR (p.completed_at, p.match_id) < ($2::timestamptz, $3::uuid))
        ORDER BY p.completed_at DESC, p.match_id DESC
        LIMIT $4`,
        [userId, after?.[0] ?? null, after?.[1] ?? null, limit + 1],
      )
      .catch((err: Error) => {
        if (after)
          throw Invalid('cursor is not valid', [{ path: 'cursor', message: 'invalid cursor' }]);
        throw err;
      });

    const page = rows.slice(0, limit);
    const last = page[page.length - 1];
    return {
      items: page.map((r) => ({
        matchId: r.match_id,
        mapVersion: r.map_version,
        mode: r.mode,
        completedAt: r.completed_at.toISOString(),
        placement: r.placement,
        players: r.players,
        kills: r.kills,
        survivalSeconds: r.survival_seconds,
        xpAwarded: r.xp_awarded,
        coinsAwarded: r.coins_awarded,
      })),
      nextCursor:
        rows.length > limit && last
          ? Buffer.from(JSON.stringify([last.completed_at_text, last.match_id])).toString(
              'base64url',
            )
          : null,
    };
  }
}
