import {
  Body,
  Controller,
  Delete,
  Get,
  HttpCode,
  Inject,
  Param,
  Post,
  Req,
  UseGuards,
} from '@nestjs/common';
import { z } from 'zod';
import { Conflict, NotFound, parse } from '../common/api-error';
import {
  AuthedRequest,
  AuthGuard,
  CurrentUser,
  RateLimit,
  RateLimitGuard,
  ServerSignatureGuard,
} from '../common/guards';
import { parseUuid } from '../common/schemas';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';
import { MatchmakingService } from './matchmaking.service';
import { ResultsService } from './results.service';
import { GameConfigService } from '../store/game-config.service';

const region = z.string().regex(/^[A-Z]{2}-[A-Z]{2,10}$/);
const queueBody = z
  .object({
    mode: z.literal('SOLO'),
    region,
    clientVersion: z.string().regex(/^\d+\.\d+\.\d+$/),
    partyId: z.null().optional(),
  })
  .strict();

@Controller('matchmaking')
@UseGuards(AuthGuard, RateLimitGuard)
export class MatchmakingController {
  constructor(
    private readonly mm: MatchmakingService,
    private readonly gameConfig: GameConfigService,
  ) {}

  @Post('queue')
  @HttpCode(200)
  @RateLimit({ limit: 30, windowSeconds: 60, key: 'user' })
  async queue(@CurrentUser() userId: string, @Body() body: unknown) {
    const request = parse(queueBody, body);
    await this.gameConfig.assertMatchmakingOpen();
    return this.mm.enqueue(userId, request);
  }

  @Delete('queue')
  @HttpCode(204)
  async leave(@CurrentUser() userId: string): Promise<void> {
    await this.mm.leave(userId);
  }

  @Get('status')
  @RateLimit({ limit: 120, windowSeconds: 60, key: 'user' })
  status(@CurrentUser() userId: string) {
    return this.mm.status(userId);
  }
}

@Controller('matches')
@UseGuards(AuthGuard)
export class MatchesController {
  constructor(
    private readonly mm: MatchmakingService,
    private readonly db: Db,
  ) {}

  @Post(':matchId/reconnect-ticket')
  @HttpCode(200)
  reconnect(@CurrentUser() userId: string, @Param('matchId') matchId: string) {
    return this.mm.reconnect(userId, parseUuid(matchId, 'matchId'));
  }

  @Get(':matchId/result')
  async result(@CurrentUser() userId: string, @Param('matchId') matchId: string) {
    const id = parseUuid(matchId, 'matchId');
    const m = await this.db.one<{
      status: string;
      completion_reason: string;
      winner_user_id: string | null;
      duration_seconds: number;
      map_version: string;
    }>(
      `SELECT m.status, m.completion_reason, m.winner_user_id, m.duration_seconds, m.map_version
         FROM matches m JOIN match_participants p ON p.match_id = m.id WHERE m.id = $1 AND p.user_id = $2`,
      [id, userId],
    );
    if (!m) throw NotFound('Match');
    if (m.status !== 'COMPLETED')
      throw Conflict('MATCH_NOT_FINISHED', 'The match has no result yet');
    const players = await this.db.query<{
      user_id: string;
      display_name: string;
      placement: number;
      kills: number;
    }>(
      `SELECT p.user_id, pp.display_name, p.placement, p.kills FROM match_participants p
         JOIN player_profiles pp ON pp.user_id = p.user_id
        WHERE p.match_id = $1 AND p.placement IS NOT NULL ORDER BY p.placement`,
      [id],
    );
    return {
      matchId: id,
      status: 'COMPLETED',
      reason: m.completion_reason,
      winnerPlayerId: m.winner_user_id,
      durationSeconds: m.duration_seconds,
      mapVersion: m.map_version,
      participants: players.map((p) => ({
        playerId: p.user_id,
        displayName: p.display_name,
        placement: p.placement,
        kills: p.kills,
      })),
    };
  }
}

const heartbeatBody = z
  .object({
    address: z.string().regex(/^[A-Za-z0-9.:-]{3,100}:[0-9]{1,5}$/),
    region,
    buildVersion: z.string().max(40),
    mapVersions: z.array(z.string().max(60)).min(1).max(20),
    state: z.enum(['IDLE', 'HOSTING']),
    matchId: z.string().uuid().nullable().optional(),
    matchPhase: z.enum(['WAITING', 'COUNTDOWN', 'ACTIVE', 'COMPLETED']).nullable().optional(),
    connectedPlayers: z.number().int().min(0).max(1000).optional(),
  })
  .strict();

const resultBody = z
  .object({
    mapVersion: z.string().max(60),
    reason: z.enum(['WINNER', 'TIMEOUT', 'CANCELLED']),
    winnerAccountId: z.string().uuid().nullable().optional(),
    durationSeconds: z.number().int().min(0).max(7200),
    participants: z
      .array(
        z
          .object({
            accountId: z.string().uuid(),
            placement: z.number().int().min(1).max(100),
            kills: z.number().int().min(0).max(99),
            survivalSeconds: z.number().int().min(0).max(7200),
          })
          .strict(),
      )
      .min(1)
      .max(100),
  })
  .strict();

/** Match-server endpoints. Every request is HMAC-signed (ServerSignatureGuard). */
@Controller('internal')
@UseGuards(ServerSignatureGuard)
export class InternalController {
  constructor(
    private readonly db: Db,
    private readonly results: ResultsService,
    private readonly gameConfig: GameConfigService,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {}

  @Post('game-servers/heartbeat')
  @HttpCode(200)
  async heartbeat(@Req() req: AuthedRequest, @Body() body: unknown) {
    const hb = parse(heartbeatBody, body);
    // The signed X-RWBR-Server header and the body must name the same server.
    if (hb.address !== req.serverAddress)
      throw Conflict('ADDRESS_MISMATCH', 'Body address does not match X-RWBR-Server');

    return this.db.tx(async (c) => {
      const { rows } = await c.query<{ id: string }>(
        `INSERT INTO game_servers (address, region, build_version, map_versions, reported_state, connected_players, last_heartbeat_at, status)
         VALUES ($1, $2, $3, $4, $5, $6, now(), 'AVAILABLE')
         ON CONFLICT (address) DO UPDATE SET region = EXCLUDED.region, build_version = EXCLUDED.build_version,
           map_versions = EXCLUDED.map_versions, reported_state = EXCLUDED.reported_state,
           connected_players = EXCLUDED.connected_players, last_heartbeat_at = now(), status = 'AVAILABLE'
         RETURNING id`,
        [
          hb.address,
          hb.region,
          hb.buildVersion,
          hb.mapVersions,
          hb.state,
          hb.connectedPlayers ?? 0,
        ],
      );
      const serverId = rows[0]!.id;

      if (hb.state === 'HOSTING' && hb.matchId) {
        await c.query(
          `UPDATE matches SET hosted_at = COALESCE(hosted_at, now()) WHERE id = $1 AND game_server_id = $2 AND status IN ('FORMING', 'ACTIVE')`,
          [hb.matchId, serverId],
        );
        if (hb.matchPhase === 'ACTIVE') {
          await c.query(
            `UPDATE matches SET status = 'ACTIVE', started_at = now() WHERE id = $1 AND game_server_id = $2 AND status = 'FORMING'`,
            [hb.matchId, serverId],
          );
        }
        return { serverId, assignment: null };
      }

      // IDLE: anything we thought it was running is gone (crash or restart).
      await c.query(
        `UPDATE matches SET status = CASE WHEN status = 'FORMING' THEN 'CANCELLED' ELSE 'TERMINATED' END, ended_at = now()
          WHERE game_server_id = $1 AND status IN ('FORMING', 'ACTIVE') AND hosted_at IS NOT NULL`,
        [serverId],
      );
      await c.query(
        `DELETE FROM matchmaking_queue q USING matches m
          WHERE q.match_id = m.id AND m.game_server_id = $1 AND m.status IN ('CANCELLED', 'TERMINATED')`,
        [serverId],
      );

      const next = await c.query<{ id: string; map_version: string; capacity: number }>(
        `SELECT id, map_version, capacity FROM matches
          WHERE game_server_id = $1 AND status = 'FORMING' AND hosted_at IS NULL ORDER BY created_at LIMIT 1`,
        [serverId],
      );
      const m = next.rows[0];
      return {
        serverId,
        assignment: m
          ? {
              matchId: m.id,
              mapVersion: m.map_version,
              capacity: m.capacity,
              minPlayers: Math.min(this.config.MATCH_MIN_PLAYERS, m.capacity),
              fillWaitSeconds: this.config.MATCH_FILL_WAIT_SECONDS,
              // Kill switch: weapons removed from this match's loot and starting kit.
              disabledWeapons: (await this.gameConfig.current()).config.disabledWeapons,
            }
          : null,
      };
    });
  }

  @Post('matches/:matchId/result')
  @HttpCode(200)
  result(@Req() req: AuthedRequest, @Param('matchId') matchId: string, @Body() body: unknown) {
    const id = parseUuid(matchId, 'matchId');
    return this.results.submit(
      req.serverAddress!,
      id,
      req.rawBody ?? Buffer.alloc(0),
      parse(resultBody, body),
    );
  }
}
