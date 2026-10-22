import { Inject, Injectable } from '@nestjs/common';
import { PoolClient } from 'pg';
import { assertActive } from '../auth/auth.service';
import { Conflict, NotFound } from '../common/api-error';
import { compareVersions } from '../common/schemas';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';
import { issueTicket } from '../tickets/join-ticket';

export interface Seat {
  matchId: string;
  server: string;
  ticket: string;
  sessionKey: string;
  expiresAt: string;
  mapVersion: string;
}

export type QueueStatus =
  | { state: 'IDLE' }
  | { state: 'QUEUED'; enqueuedAt: string; region: string }
  | { state: 'MATCHED'; seat: Seat };

interface QueueRow {
  user_id: string;
  mode: string;
  region: string;
  enqueued_at: Date;
  match_id: string | null;
}

/**
 * Matchmaking on PostgreSQL (row locks with SKIP LOCKED), so several API instances can run without Redis.
 * Players are placed into FORMING matches on live servers; a seat (signed join ticket) is only handed out once
 * the server has confirmed it is hosting that match.
 */
@Injectable()
export class MatchmakingService {
  private readonly ticketSecret: Buffer;

  constructor(
    private readonly db: Db,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {
    this.ticketSecret = Buffer.from(config.TICKET_SECRET_HEX, 'hex');
  }

  async enqueue(
    userId: string,
    req: { mode: 'SOLO'; region: string; clientVersion: string },
  ): Promise<QueueStatus> {
    if (compareVersions(req.clientVersion, this.config.MIN_CLIENT_VERSION) < 0) {
      throw Conflict(
        'CLIENT_OUTDATED',
        `Client ${req.clientVersion} is too old; update to ${this.config.MIN_CLIENT_VERSION} or later`,
      );
    }
    const user = await this.db.one<{ status: string }>('SELECT status FROM users WHERE id = $1', [
      userId,
    ]);
    if (!user) throw NotFound('Player');
    assertActive(user.status);

    await this.db.query(
      `INSERT INTO matchmaking_queue (user_id, mode, region, client_version) VALUES ($1, $2, $3, $4)
       ON CONFLICT (user_id) DO UPDATE SET mode = EXCLUDED.mode, region = EXCLUDED.region,
         client_version = EXCLUDED.client_version
       WHERE matchmaking_queue.match_id IS NULL`,
      [userId, req.mode, req.region, req.clientVersion],
    );
    return this.status(userId);
  }

  async leave(userId: string): Promise<void> {
    await this.db.tx(async (c) => {
      const { rows } = await c.query<{ match_id: string | null; status: string | null }>(
        `SELECT q.match_id, m.status FROM matchmaking_queue q LEFT JOIN matches m ON m.id = q.match_id
          WHERE q.user_id = $1 FOR UPDATE OF q`,
        [userId],
      );
      const q = rows[0];
      if (!q) return;
      if (q.match_id && q.status !== 'FORMING') {
        throw Conflict('MATCH_IN_PROGRESS', 'The match has started; leave from inside the game');
      }
      if (q.match_id)
        await c.query('DELETE FROM match_participants WHERE match_id = $1 AND user_id = $2', [
          q.match_id,
          userId,
        ]);
      await c.query('DELETE FROM matchmaking_queue WHERE user_id = $1', [userId]);
    });
  }

  /** Current state; tries to place a queued player. Called by clients every 1-2 s while queued. */
  async status(userId: string): Promise<QueueStatus> {
    await this.tryAssign(userId);
    const row = await this.db.one<QueueRow & { status: string | null; hosted_at: Date | null }>(
      `SELECT q.*, m.status, m.hosted_at FROM matchmaking_queue q LEFT JOIN matches m ON m.id = q.match_id
        WHERE q.user_id = $1`,
      [userId],
    );
    if (!row) return { state: 'IDLE' };
    if (row.match_id && row.hosted_at && (row.status === 'FORMING' || row.status === 'ACTIVE')) {
      return { state: 'MATCHED', seat: await this.seat(userId, row.match_id) };
    }
    return { state: 'QUEUED', enqueuedAt: row.enqueued_at.toISOString(), region: row.region };
  }

  /** Fresh seat for a match the player belongs to (BRD §10 reconnect-ticket). */
  async reconnect(userId: string, matchId: string): Promise<Seat> {
    const m = await this.db.one<{ status: string; hosted_at: Date | null }>(
      `SELECT m.status, m.hosted_at FROM matches m JOIN match_participants p ON p.match_id = m.id
        WHERE m.id = $1 AND p.user_id = $2`,
      [matchId, userId],
    );
    if (!m) throw NotFound('Match');
    if (!m.hosted_at || (m.status !== 'FORMING' && m.status !== 'ACTIVE')) {
      throw Conflict('MATCH_NOT_JOINABLE', 'This match is not running');
    }
    return this.seat(userId, matchId);
  }

  private async seat(userId: string, matchId: string): Promise<Seat> {
    const row = await this.db.one<{ address: string; map_version: string; display_name: string }>(
      `SELECT s.address, m.map_version, p.display_name
         FROM matches m JOIN game_servers s ON s.id = m.game_server_id, player_profiles p
        WHERE m.id = $1 AND p.user_id = $2`,
      [matchId, userId],
    );
    if (!row) throw NotFound('Match');
    const expiresAt = new Date(Date.now() + this.config.SEAT_TTL_SECONDS * 1000);
    const t = issueTicket(this.ticketSecret, matchId, userId, row.display_name, expiresAt);
    return {
      matchId,
      server: row.address,
      ticket: t.ticket.toString('base64'),
      sessionKey: t.sessionKey.toString('base64'),
      expiresAt: expiresAt.toISOString(),
      mapVersion: row.map_version,
    };
  }

  private async tryAssign(userId: string): Promise<void> {
    await this.db.tx(async (c) => {
      const { rows } = await c.query<QueueRow>(
        'SELECT * FROM matchmaking_queue WHERE user_id = $1 FOR UPDATE SKIP LOCKED',
        [userId],
      );
      const q = rows[0];
      if (!q || q.match_id) return;

      const matchId = (await this.findFormingMatch(c, q)) ?? (await this.createMatch(c, q));
      if (!matchId) return; // no server free: stay queued
      await c.query('INSERT INTO match_participants (match_id, user_id) VALUES ($1, $2)', [
        matchId,
        userId,
      ]);
      await c.query(
        'UPDATE matchmaking_queue SET match_id = $2, matched_at = now() WHERE user_id = $1',
        [userId, matchId],
      );
    });
  }

  private async findFormingMatch(c: PoolClient, q: QueueRow): Promise<string | null> {
    const { rows } = await c.query<{ id: string }>(
      `SELECT m.id FROM matches m JOIN game_servers s ON s.id = m.game_server_id
        WHERE m.status = 'FORMING' AND m.region = $1 AND m.mode = $2
          AND s.status = 'AVAILABLE' AND s.last_heartbeat_at > now() - make_interval(secs => $3)
          AND (SELECT count(*) FROM match_participants p WHERE p.match_id = m.id) < m.capacity
        ORDER BY m.created_at
        LIMIT 1 FOR UPDATE OF m SKIP LOCKED`,
      [q.region, q.mode, this.config.SERVER_HEARTBEAT_STALE_SECONDS],
    );
    return rows[0]?.id ?? null;
  }

  private async createMatch(c: PoolClient, q: QueueRow): Promise<string | null> {
    const { rows } = await c.query<{ id: string }>(
      `SELECT s.id FROM game_servers s
        WHERE s.region = $1 AND s.status = 'AVAILABLE' AND s.reported_state = 'IDLE'
          AND s.last_heartbeat_at > now() - make_interval(secs => $2)
          AND $3 = ANY (s.map_versions)
          AND NOT EXISTS (SELECT 1 FROM matches m WHERE m.game_server_id = s.id AND m.status IN ('FORMING', 'ACTIVE'))
        ORDER BY s.last_heartbeat_at DESC
        LIMIT 1 FOR UPDATE OF s SKIP LOCKED`,
      [q.region, this.config.SERVER_HEARTBEAT_STALE_SECONDS, this.config.DEFAULT_MAP_VERSION],
    );
    const server = rows[0];
    if (!server) return null;
    const created = await c.query<{ id: string }>(
      `INSERT INTO matches (game_server_id, map_version, mode, region, capacity, ruleset_version)
       VALUES ($1, $2, $3, $4, $5, 'v1') RETURNING id`,
      [server.id, this.config.DEFAULT_MAP_VERSION, q.mode, q.region, this.config.MATCH_CAPACITY],
    );
    return created.rows[0]!.id;
  }
}
