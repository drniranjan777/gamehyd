import { Inject, Injectable } from '@nestjs/common';
import { PoolClient } from 'pg';
import { assertActive } from '../auth/auth.service';
import { Conflict, NotFound } from '../common/api-error';
import { compareVersions } from '../common/schemas';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';
import { issueTicket, NO_TEAM } from '../tickets/join-ticket';
import { Mode, TEAM_SIZE } from './parties';

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
  mode: Mode;
  region: string;
  enqueued_at: Date;
  match_id: string | null;
  party_id: string | null;
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
    req: { mode: Mode; region: string; clientVersion: string },
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

    // In a party, the leader queues everyone together (Duo or Squad, if the party fits the team).
    const party = await this.db.one<{ id: string; leader_id: string }>(
      `SELECT p.id, p.leader_id FROM parties p JOIN party_members m ON m.party_id = p.id WHERE m.user_id = $1`,
      [userId],
    );
    let members = [userId];
    if (party) {
      if (party.leader_id !== userId)
        throw Conflict('PARTY_LEADER_QUEUES', 'Your party leader starts matchmaking for the party');
      if (req.mode === 'SOLO') throw Conflict('PARTY_IN_SOLO', 'Leave the party to play Solo');
      const rows = await this.db.query<{ user_id: string; status: string }>(
        `SELECT m.user_id, u.status FROM party_members m JOIN users u ON u.id = m.user_id WHERE m.party_id = $1`,
        [party.id],
      );
      if (rows.length > TEAM_SIZE[req.mode])
        throw Conflict(
          'PARTY_TOO_BIG',
          `A ${req.mode.toLowerCase()} team has room for ${TEAM_SIZE[req.mode]}`,
        );
      for (const r of rows) assertActive(r.status);
      members = rows.map((r) => r.user_id);
    }

    for (const member of members)
      await this.db.query(
        `INSERT INTO matchmaking_queue (user_id, mode, region, client_version, party_id) VALUES ($1, $2, $3, $4, $5)
         ON CONFLICT (user_id) DO UPDATE SET mode = EXCLUDED.mode, region = EXCLUDED.region,
           client_version = EXCLUDED.client_version, party_id = EXCLUDED.party_id
         WHERE matchmaking_queue.match_id IS NULL`,
        [member, req.mode, req.region, req.clientVersion, party?.id ?? null],
      );
    return this.status(userId);
  }

  async leave(userId: string): Promise<void> {
    await this.db.tx(async (c) => {
      const { rows } = await c.query<{
        match_id: string | null;
        status: string | null;
        party_id: string | null;
      }>(
        `SELECT q.match_id, m.status, q.party_id FROM matchmaking_queue q LEFT JOIN matches m ON m.id = q.match_id
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
      // A party searches together: one member stopping stops the party's search.
      if (q.party_id && !q.match_id)
        await c.query('DELETE FROM matchmaking_queue WHERE party_id = $1 AND match_id IS NULL', [
          q.party_id,
        ]);
    });
  }

  /** Current state; tries to place a queued player. Called by clients every 1-2 s while queued. */
  private lastSweep = 0;

  /**
   * Cancels FORMING matches whose assigned server never confirmed hosting (it died or lost the network) and puts
   * their players back in the queue (risk 16). Cheap and idempotent; runs at most every 5 s per API instance.
   */
  async sweepUnhosted(force = false): Promise<number> {
    if (!force && Date.now() - this.lastSweep < 5_000) return 0;
    this.lastSweep = Date.now();
    const rows = await this.db.query<{ id: string }>(
      `WITH stale AS (
         UPDATE matches SET status = 'CANCELLED', completion_reason = 'CANCELLED', ended_at = now()
          WHERE status = 'FORMING' AND hosted_at IS NULL
            AND created_at < now() - make_interval(secs => $1::int)
          RETURNING id),
       requeued AS (
         UPDATE matchmaking_queue SET match_id = NULL, matched_at = NULL
          WHERE match_id IN (SELECT id FROM stale) RETURNING user_id)
       SELECT id FROM stale`,
      [this.config.UNHOSTED_MATCH_TIMEOUT_SECONDS],
    );
    return rows.length;
  }

  async status(userId: string): Promise<QueueStatus> {
    await this.sweepUnhosted();
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
    const row = await this.db.one<{
      address: string;
      map_version: string;
      display_name: string;
      team: number | null;
    }>(
      `SELECT s.address, m.map_version, p.display_name, mp.team
         FROM matches m JOIN game_servers s ON s.id = m.game_server_id
         JOIN match_participants mp ON mp.match_id = m.id AND mp.user_id = $2, player_profiles p
        WHERE m.id = $1 AND p.user_id = $2`,
      [matchId, userId],
    );
    if (!row) throw NotFound('Match');
    const expiresAt = new Date(Date.now() + this.config.SEAT_TTL_SECONDS * 1000);
    // The ticket names the player's team, so the match server keeps parties together.
    const t = issueTicket(
      this.ticketSecret,
      matchId,
      userId,
      row.display_name,
      expiresAt,
      undefined,
      row.team ?? NO_TEAM,
    );
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

      // A party is placed together: all its queued members, in one match and one team.
      let group: QueueRow[] = [q];
      if (q.party_id) {
        const party = await c.query<QueueRow>(
          'SELECT * FROM matchmaking_queue WHERE party_id = $1 AND match_id IS NULL ORDER BY user_id FOR UPDATE',
          [q.party_id],
        );
        group = party.rows;
      }

      const matchId =
        (await this.findFormingMatch(c, q, group.length)) ?? (await this.createMatch(c, q));
      if (!matchId) return; // no server free: stay queued
      const team = await this.teamFor(c, matchId, q.mode, q.party_id !== null);
      for (const member of group) {
        await c.query(
          'INSERT INTO match_participants (match_id, user_id, enqueued_at, team, party_id) VALUES ($1, $2, $3, $4, $5)',
          [matchId, member.user_id, member.enqueued_at, team, member.party_id],
        );
        await c.query(
          'UPDATE matchmaking_queue SET match_id = $2, matched_at = now() WHERE user_id = $1',
          [member.user_id, matchId],
        );
      }
    });
  }

  /**
   * The team for a newcomer: a party gets a new team of its own; a solo player in Duo or Squad joins a team of other
   * solo players that still has room, else starts one; in Solo everyone is their own team.
   */
  private async teamFor(
    c: PoolClient,
    matchId: string,
    mode: Mode,
    party: boolean,
  ): Promise<number> {
    if (!party && mode !== 'SOLO') {
      const open = await c.query<{ team: number }>(
        `SELECT team FROM match_participants WHERE match_id = $1 AND team IS NOT NULL
          GROUP BY team HAVING count(*) < $2 AND bool_and(party_id IS NULL) ORDER BY team LIMIT 1`,
        [matchId, TEAM_SIZE[mode]],
      );
      if (open.rows[0]) return open.rows[0].team;
    }
    const next = await c.query<{ team: number }>(
      'SELECT COALESCE(max(team) + 1, 0) AS team FROM match_participants WHERE match_id = $1',
      [matchId],
    );
    return Number(next.rows[0]!.team);
  }

  private async findFormingMatch(
    c: PoolClient,
    q: QueueRow,
    seats: number,
  ): Promise<string | null> {
    const { rows } = await c.query<{ id: string }>(
      `SELECT m.id FROM matches m JOIN game_servers s ON s.id = m.game_server_id
        WHERE m.status = 'FORMING' AND m.region = $1 AND m.mode = $2
          AND s.status = 'AVAILABLE' AND s.last_heartbeat_at > now() - make_interval(secs => $3)
          AND (SELECT count(*) FROM match_participants p WHERE p.match_id = m.id) + $4 <= m.capacity
        ORDER BY m.created_at
        LIMIT 1 FOR UPDATE OF m SKIP LOCKED`,
      [q.region, q.mode, this.config.SERVER_HEARTBEAT_STALE_SECONDS, seats],
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
