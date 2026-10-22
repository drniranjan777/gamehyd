import { Injectable } from '@nestjs/common';
import { createHash } from 'node:crypto';
import { PoolClient } from 'pg';
import { ApiError, Conflict, Invalid, NotFound, Unauthorized } from '../common/api-error';
import { Db } from '../database/database.module';
import { levelFor, matchReward } from '../player/progression';
import { addSeasonXp } from '../seasons/seasons.service';
import { GameConfigService } from '../store/game-config.service';

export interface ResultSubmission {
  mapVersion: string;
  reason: 'WINNER' | 'TIMEOUT' | 'CANCELLED';
  winnerAccountId?: string | null;
  durationSeconds: number;
  participants: { accountId: string; placement: number; kills: number; survivalSeconds: number }[];
}

type Outcome = 'ACCEPTED' | 'DUPLICATE' | 'REJECTED';

/**
 * Accepts final results from the hosting match server only (BRD §5.1: the server decides; §14: forged results
 * must not alter records). Every submission is recorded in match_verification_records. Rewards go through the
 * wallet ledger with an idempotency key, so a retried submission can never pay twice.
 */
@Injectable()
export class ResultsService {
  constructor(
    private readonly db: Db,
    private readonly gameConfig: GameConfigService,
  ) {}

  async submit(
    serverAddress: string,
    matchId: string,
    raw: Buffer,
    r: ResultSubmission,
  ): Promise<{ matchId: string; duplicate: boolean }> {
    const hash = createHash('sha256').update(raw).digest();
    const server = await this.db.one<{ id: string }>(
      'SELECT id FROM game_servers WHERE address = $1',
      [serverAddress],
    );

    try {
      const duplicate = await this.db.tx(async (c) => {
        const { rows } = await c.query<{
          game_server_id: string;
          status: string;
          map_version: string;
          result_sha256: Buffer | null;
        }>(
          'SELECT game_server_id, status, map_version, result_sha256 FROM matches WHERE id = $1 FOR UPDATE',
          [matchId],
        );
        const m = rows[0];
        if (!m) throw NotFound('Match');
        if (!server || m.game_server_id !== server.id)
          throw Unauthorized('NOT_HOSTING_SERVER', 'This server did not host the match');
        if (m.status === 'COMPLETED') {
          if (m.result_sha256 && m.result_sha256.equals(hash)) return true;
          throw Conflict(
            'RESULT_CONFLICT',
            'A different result was already accepted for this match',
          );
        }
        if (m.status !== 'FORMING' && m.status !== 'ACTIVE')
          throw Conflict('MATCH_CLOSED', `Match is ${m.status}`);
        if (r.mapVersion !== m.map_version) throw Invalid('mapVersion does not match the match');
        await this.validateAgainstRoster(c, matchId, r);
        await this.apply(c, matchId, r, hash);
        return false;
      });
      await this.record(matchId, server?.id, hash, duplicate ? 'DUPLICATE' : 'ACCEPTED', null);
      return { matchId, duplicate };
    } catch (err) {
      await this.record(
        matchId,
        server?.id,
        hash,
        'REJECTED',
        err instanceof Error ? err.message : 'error',
      );
      throw err;
    }
  }

  private async validateAgainstRoster(
    c: PoolClient,
    matchId: string,
    r: ResultSubmission,
  ): Promise<void> {
    const { rows } = await c.query<{ user_id: string }>(
      'SELECT user_id FROM match_participants WHERE match_id = $1',
      [matchId],
    );
    const roster = new Set(rows.map((x) => x.user_id));
    const n = r.participants.length;
    const ids = new Set(r.participants.map((p) => p.accountId));
    const placements = new Set(r.participants.map((p) => p.placement));

    const problems: string[] = [];
    if (ids.size !== n) problems.push('duplicate accountId');
    for (const p of r.participants)
      if (!roster.has(p.accountId))
        problems.push(`account ${p.accountId} was not assigned to this match`);
    if (placements.size !== n || [...placements].some((x) => x < 1 || x > n))
      problems.push('placements must be 1..n, each once');
    if (r.participants.reduce((s, p) => s + p.kills, 0) > n - 1)
      problems.push('more eliminations than opponents');
    if (r.participants.some((p) => p.survivalSeconds > r.durationSeconds))
      problems.push('survival longer than the match');
    const first = r.participants.find((p) => p.placement === 1);
    if (r.reason === 'WINNER' && (!r.winnerAccountId || first?.accountId !== r.winnerAccountId))
      problems.push('winner must hold placement 1');
    if (r.reason !== 'WINNER' && r.winnerAccountId)
      problems.push('only WINNER results name a winner');
    if (problems.length)
      throw new ApiError(
        422,
        'RESULT_REJECTED',
        'Result failed validation',
        problems.map((m) => ({ path: 'participants', message: m })),
      );
  }

  private async apply(
    c: PoolClient,
    matchId: string,
    r: ResultSubmission,
    hash: Buffer,
  ): Promise<void> {
    const n = r.participants.length;
    await c.query(
      `UPDATE matches SET status = 'COMPLETED', completion_reason = $2, winner_user_id = $3, duration_seconds = $4::int,
              result_sha256 = $5, ended_at = now(), started_at = COALESCE(started_at, now() - make_interval(secs => $4::int))
        WHERE id = $1`,
      [matchId, r.reason, r.winnerAccountId ?? null, r.durationSeconds, hash],
    );

    // Double-XP events: the multiplier in force when the result arrives (retries are duplicates and pay nothing).
    const { xpMultiplier } = (await this.gameConfig.current()).config;
    for (const p of r.participants) {
      const won = r.reason === 'WINNER' && p.accountId === r.winnerAccountId;
      const base = matchReward(p.placement, n, p.kills, won);
      const reward = { ...base, xp: Math.round(base.xp * xpMultiplier) };
      await c.query(
        `UPDATE match_participants SET placement = $3, kills = $4, survival_seconds = $5, xp_awarded = $6,
                coins_awarded = $7, completed_at = now()
          WHERE match_id = $1 AND user_id = $2`,
        [matchId, p.accountId, p.placement, p.kills, p.survivalSeconds, reward.xp, reward.coins],
      );
      const { rows } = await c.query<{ xp: string }>(
        `UPDATE player_profiles SET xp = xp + $2, matches_played = matches_played + 1, wins = wins + $3, kills = kills + $4
          WHERE user_id = $1 RETURNING xp`,
        [p.accountId, reward.xp, won ? 1 : 0, p.kills],
      );
      await addSeasonXp(c, p.accountId, reward.xp);
      await c.query('UPDATE player_profiles SET level = $2 WHERE user_id = $1', [
        p.accountId,
        levelFor(Number(rows[0]?.xp ?? 0)),
      ]);
      await credit(
        c,
        p.accountId,
        'COIN',
        reward.coins,
        'MATCH_REWARD',
        `match:${matchId}`,
        `match:${matchId}:reward`,
      );
    }

    // Assigned players who never joined get no result row; everyone leaves the queue.
    await c.query('DELETE FROM matchmaking_queue WHERE match_id = $1', [matchId]);
  }

  private async record(
    matchId: string,
    serverId: string | undefined,
    hash: Buffer,
    outcome: Outcome,
    reason: string | null,
  ): Promise<void> {
    await this.db.query(
      `INSERT INTO match_verification_records (match_id, game_server_id, payload_sha256, signature_valid, outcome, reason)
       VALUES ($1, $2, $3, true, $4, $5)`,
      [matchId, serverId ?? null, hash, outcome, reason?.slice(0, 300) ?? null],
    );
  }
}

/**
 * Ledger credit: lock the wallet row, write exactly one ledger row for the next version, update the projection.
 * A repeated idempotency key is a no-op (BRD §9 schema notes).
 */
export async function credit(
  c: PoolClient,
  userId: string,
  currency: 'COIN' | 'GEM',
  amount: number,
  reason: string,
  reference: string,
  idempotencyKey: string,
): Promise<void> {
  if (amount === 0) return;
  await c.query(
    `INSERT INTO wallets (user_id, currency_code) VALUES ($1, $2) ON CONFLICT DO NOTHING`,
    [userId, currency],
  );
  const { rows } = await c.query<{ id: string; balance: string; version: string }>(
    'SELECT id, balance, version FROM wallets WHERE user_id = $1 AND currency_code = $2 FOR UPDATE',
    [userId, currency],
  );
  const w = rows[0]!;
  const seen = await c.query(
    'SELECT 1 FROM wallet_transactions WHERE wallet_id = $1 AND idempotency_key = $2',
    [w.id, idempotencyKey],
  );
  if (seen.rowCount) return;
  const version = Number(w.version) + 1;
  const balance = Number(w.balance) + amount;
  await c.query(
    `INSERT INTO wallet_transactions (wallet_id, amount, balance_after, wallet_version, reason, reference, idempotency_key)
     VALUES ($1, $2, $3, $4, $5, $6, $7)`,
    [w.id, amount, balance, version, reason, reference, idempotencyKey],
  );
  await c.query('UPDATE wallets SET balance = $2, version = $3 WHERE id = $1', [
    w.id,
    balance,
    version,
  ]);
}
