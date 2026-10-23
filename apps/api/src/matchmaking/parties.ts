import {
  Body,
  Controller,
  Delete,
  Get,
  HttpCode,
  Injectable,
  Post,
  Put,
  UseGuards,
} from '@nestjs/common';
import { randomInt } from 'node:crypto';
import { PoolClient } from 'pg';
import { z } from 'zod';
import { Conflict, Forbidden, NotFound, parse } from '../common/api-error';
import { AuthGuard, CurrentUser, RateLimit, RateLimitGuard } from '../common/guards';
import { Db } from '../database/database.module';

export type Mode = 'SOLO' | 'DUO' | 'SQUAD';

/** Players per team in each mode. */
export const TEAM_SIZE: Record<Mode, number> = { SOLO: 1, DUO: 2, SQUAD: 4 };

export interface PartyMember {
  accountId: string;
  displayName: string;
  leader: boolean;
}

export interface Party {
  id: string;
  code: string;
  mode: Mode;
  leaderId: string;
  members: PartyMember[];
}

/** Codes are six characters without 0/O or 1/I, easy to read out. */
const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';

/**
 * Parties for Duo and Squad (Phase C part 6c). One player creates a party and shares its code; friends join with it;
 * the leader picks the mode and queues for everyone, and matchmaking keeps the party in one match and one team.
 * A player is in at most one party; when the leader leaves, the longest-standing member leads, and an empty party
 * is deleted.
 */
@Injectable()
export class PartiesService {
  constructor(private readonly db: Db) {}

  async current(userId: string): Promise<Party | null> {
    const row = await this.db.one<{ party_id: string }>(
      'SELECT party_id FROM party_members WHERE user_id = $1',
      [userId],
    );
    return row ? this.load(row.party_id) : null;
  }

  async create(userId: string, mode: Mode): Promise<Party> {
    if (mode === 'SOLO') throw Conflict('PARTY_MODE', 'Parties are for Duo and Squad');
    const id = await this.db.tx(async (c) => {
      await this.leaveIn(c, userId);
      for (let attempt = 0; attempt < 10; attempt++) {
        const code = Array.from(
          { length: 6 },
          () => CODE_ALPHABET[randomInt(CODE_ALPHABET.length)],
        ).join('');
        const { rows } = await c.query<{ id: string }>(
          `INSERT INTO parties (code, leader_id, mode) VALUES ($1, $2, $3)
           ON CONFLICT (code) DO NOTHING RETURNING id`,
          [code, userId, mode],
        );
        if (rows[0]) {
          await c.query('INSERT INTO party_members (party_id, user_id) VALUES ($1, $2)', [
            rows[0].id,
            userId,
          ]);
          return rows[0].id;
        }
      }
      throw Conflict('PARTY_CODE', 'Could not make a party code; try again');
    });
    return (await this.load(id))!;
  }

  async join(userId: string, code: string): Promise<Party> {
    const id = await this.db.tx(async (c) => {
      const { rows } = await c.query<{ id: string; mode: Mode }>(
        'SELECT id, mode FROM parties WHERE code = $1 FOR UPDATE',
        [code.toUpperCase()],
      );
      const party = rows[0];
      if (!party) throw NotFound('Party');
      const mine = await c.query<{ party_id: string }>(
        'SELECT party_id FROM party_members WHERE user_id = $1',
        [userId],
      );
      if (mine.rows[0]?.party_id === party.id) return party.id;
      const size = await c.query<{ n: string }>(
        'SELECT count(*) AS n FROM party_members WHERE party_id = $1',
        [party.id],
      );
      if (Number(size.rows[0]!.n) >= TEAM_SIZE[party.mode])
        throw Conflict('PARTY_FULL', `This ${party.mode.toLowerCase()} party is full`);
      if (await this.queuedIn(c, party.id))
        throw Conflict('PARTY_QUEUED', 'This party is already looking for a match');
      await this.leaveIn(c, userId);
      await c.query('INSERT INTO party_members (party_id, user_id) VALUES ($1, $2)', [
        party.id,
        userId,
      ]);
      return party.id;
    });
    return (await this.load(id))!;
  }

  async leave(userId: string): Promise<void> {
    await this.db.tx((c) => this.leaveIn(c, userId));
  }

  async setMode(userId: string, mode: Mode): Promise<Party> {
    if (mode === 'SOLO') throw Conflict('PARTY_MODE', 'Parties are for Duo and Squad');
    const party = await this.current(userId);
    if (!party) throw NotFound('Party');
    if (party.leaderId !== userId)
      throw Forbidden('PARTY_LEADER_ONLY', 'Only the party leader can change the mode');
    if (party.members.length > TEAM_SIZE[mode])
      throw Conflict(
        'PARTY_TOO_BIG',
        `A ${mode.toLowerCase()} team has room for ${TEAM_SIZE[mode]}`,
      );
    await this.db.query('UPDATE parties SET mode = $2 WHERE id = $1', [party.id, mode]);
    return (await this.load(party.id))!;
  }

  /** Leaves the current party (if any): out of the queue too, the next member leads, an empty party goes. */
  private async leaveIn(c: PoolClient, userId: string): Promise<void> {
    const { rows } = await c.query<{ party_id: string }>(
      'DELETE FROM party_members WHERE user_id = $1 RETURNING party_id',
      [userId],
    );
    const partyId = rows[0]?.party_id;
    if (!partyId) return;
    await c.query(
      'DELETE FROM matchmaking_queue WHERE user_id = $1 AND party_id = $2 AND match_id IS NULL',
      [userId, partyId],
    );
    const next = await c.query<{ user_id: string }>(
      'SELECT user_id FROM party_members WHERE party_id = $1 ORDER BY joined_at LIMIT 1',
      [partyId],
    );
    if (!next.rows[0]) await c.query('DELETE FROM parties WHERE id = $1', [partyId]);
    else
      await c.query('UPDATE parties SET leader_id = $2 WHERE id = $1 AND leader_id = $3', [
        partyId,
        next.rows[0].user_id,
        userId,
      ]);
  }

  private async queuedIn(c: PoolClient, partyId: string): Promise<boolean> {
    const { rows } = await c.query('SELECT 1 FROM matchmaking_queue WHERE party_id = $1 LIMIT 1', [
      partyId,
    ]);
    return rows.length > 0;
  }

  private async load(partyId: string): Promise<Party | null> {
    const party = await this.db.one<{ id: string; code: string; mode: Mode; leader_id: string }>(
      'SELECT id, code, mode, leader_id FROM parties WHERE id = $1',
      [partyId],
    );
    if (!party) return null;
    const rows = await this.db.query<{ user_id: string; display_name: string }>(
      `SELECT m.user_id, p.display_name FROM party_members m JOIN player_profiles p ON p.user_id = m.user_id
        WHERE m.party_id = $1 ORDER BY m.joined_at`,
      [partyId],
    );
    return {
      id: party.id,
      code: party.code,
      mode: party.mode,
      leaderId: party.leader_id,
      members: rows.map((r) => ({
        accountId: r.user_id,
        displayName: r.display_name,
        leader: r.user_id === party.leader_id,
      })),
    };
  }
}

const mode = z.enum(['DUO', 'SQUAD']);

@Controller('parties')
@UseGuards(AuthGuard, RateLimitGuard)
export class PartiesController {
  constructor(private readonly parties: PartiesService) {}

  @Get('current')
  async current(@CurrentUser() userId: string) {
    return { party: await this.parties.current(userId) };
  }

  @Post()
  @RateLimit({ limit: 20, windowSeconds: 60, key: 'user' })
  create(@CurrentUser() userId: string, @Body() body: unknown) {
    const req = parse(z.object({ mode: mode.default('DUO') }).strict(), body);
    return this.parties.create(userId, req.mode);
  }

  @Post('join')
  @HttpCode(200)
  @RateLimit({ limit: 20, windowSeconds: 60, key: 'user' })
  join(@CurrentUser() userId: string, @Body() body: unknown) {
    const req = parse(z.object({ code: z.string().regex(/^[A-Za-z0-9]{6}$/) }).strict(), body);
    return this.parties.join(userId, req.code);
  }

  @Delete('current')
  @HttpCode(204)
  async leave(@CurrentUser() userId: string): Promise<void> {
    await this.parties.leave(userId);
  }

  @Put('current/mode')
  setMode(@CurrentUser() userId: string, @Body() body: unknown) {
    const req = parse(z.object({ mode }).strict(), body);
    return this.parties.setMode(userId, req.mode);
  }
}
