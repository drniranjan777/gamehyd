import { Inject, Injectable } from '@nestjs/common';
import { PoolClient } from 'pg';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';
import { LATENCY_BUCKETS_MS, percentileFromBuckets } from './latency.recorder';

const sumBuckets = (rows: { buckets: number[] }[]) => {
  const out = new Array(LATENCY_BUCKETS_MS.length + 1).fill(0) as number[];
  for (const r of rows) r.buckets.forEach((n, i) => (out[i] = (out[i] ?? 0) + n));
  return out;
};

/**
 * Operations dashboards (PHASE-PLAN Phase 7): API latency p50/p95/p99 and error rate, match-server tick health,
 * capacity (concurrent players) and estimated cost per match and per concurrent-player hour.
 */
@Injectable()
export class OpsService {
  constructor(
    private readonly db: Db,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {}

  /** Called from the heartbeat transaction: one sample per server per minute (peaks kept). */
  static async recordServerSample(
    c: PoolClient,
    serverId: string,
    connectedPlayers: number,
    hosting: boolean,
    tickMsP99: number | null,
  ): Promise<void> {
    await c.query(
      `INSERT INTO server_load_samples (game_server_id, minute, connected_players, hosting, tick_ms_p99)
       VALUES ($1, date_trunc('minute', now()), $2, $3, $4)
       ON CONFLICT (game_server_id, minute) DO UPDATE SET
         connected_players = GREATEST(server_load_samples.connected_players, EXCLUDED.connected_players),
         hosting = server_load_samples.hosting OR EXCLUDED.hosting,
         tick_ms_p99 = GREATEST(server_load_samples.tick_ms_p99, EXCLUDED.tick_ms_p99)`,
      [serverId, connectedPlayers, hosting, tickMsP99],
    );
  }

  async compute(hours: number) {
    const since = `now() - make_interval(hours => $1::int)`;
    const p = [hours];

    const routes = await this.db.query<{
      route: string;
      requests: number;
      errors: number;
      buckets: number[];
    }>(
      `SELECT route, sum(requests)::int AS requests, sum(server_errors)::int AS errors,
              (SELECT array_agg(s ORDER BY i) FROM (
                 SELECT i, sum(b)::int AS s FROM api_latency_minutes l2, unnest(l2.buckets) WITH ORDINALITY AS t(b, i)
                  WHERE l2.route = l.route AND l2.minute > ${since} GROUP BY i) x) AS buckets
         FROM api_latency_minutes l WHERE minute > ${since} GROUP BY route ORDER BY sum(requests) DESC`,
      p,
    );
    const all = sumBuckets(routes);
    const requests = routes.reduce((a, r) => a + r.requests, 0);
    const errors = routes.reduce((a, r) => a + r.errors, 0);

    const servers = (await this.db.one<{
      server_minutes: number;
      hosting_minutes: number;
      player_minutes: number;
      servers: number;
      tick_p50: number | null;
      tick_p99: number | null;
      tick_max: number | null;
      over_budget: number;
    }>(
      `SELECT count(*)::int AS server_minutes, count(*) FILTER (WHERE hosting)::int AS hosting_minutes,
              COALESCE(sum(connected_players), 0)::int AS player_minutes, count(DISTINCT game_server_id)::int AS servers,
              percentile_cont(0.5) WITHIN GROUP (ORDER BY tick_ms_p99)::float AS tick_p50,
              percentile_cont(0.99) WITHIN GROUP (ORDER BY tick_ms_p99)::float AS tick_p99,
              max(tick_ms_p99)::float AS tick_max,
              count(*) FILTER (WHERE tick_ms_p99 > $2)::int AS over_budget
         FROM server_load_samples WHERE minute > ${since}`,
      [hours, this.config.TICK_BUDGET_MS],
    ))!;
    const peak = (await this.db.one<{ peak: number; at: Date | null }>(
      `SELECT COALESCE(max(ccu), 0)::int AS peak, (array_agg(minute ORDER BY ccu DESC))[1] AS at
         FROM (SELECT minute, sum(connected_players) AS ccu FROM server_load_samples WHERE minute > ${since} GROUP BY minute) m`,
      p,
    ))!;
    const matches = (await this.db.one<{ completed: number }>(
      `SELECT count(*)::int AS completed FROM matches WHERE status = 'COMPLETED' AND ended_at > ${since}`,
      p,
    ))!;

    const serverHours = servers.server_minutes / 60;
    const playerHours = servers.player_minutes / 60;
    const cost = serverHours * this.config.SERVER_COST_PER_HOUR_INR;
    return {
      hours,
      generatedAt: new Date().toISOString(),
      api: {
        requests,
        serverErrorRate: requests > 0 ? errors / requests : null,
        p50Ms: percentileFromBuckets(all, 50),
        p95Ms: percentileFromBuckets(all, 95),
        p99Ms: percentileFromBuckets(all, 99),
        routes: routes.slice(0, 30).map((r) => ({
          route: r.route,
          requests: r.requests,
          serverErrors: r.errors,
          p50Ms: percentileFromBuckets(r.buckets, 50),
          p95Ms: percentileFromBuckets(r.buckets, 95),
          p99Ms: percentileFromBuckets(r.buckets, 99),
        })),
      },
      servers: {
        distinctServers: servers.servers,
        serverHours,
        hostingShare:
          servers.server_minutes > 0 ? servers.hosting_minutes / servers.server_minutes : null,
        tickBudgetMs: this.config.TICK_BUDGET_MS,
        tickP99Median: servers.tick_p50,
        tickP99Worst: servers.tick_max,
        minutesOverBudget: servers.over_budget,
      },
      capacity: {
        peakConcurrentPlayers: peak.peak,
        peakAt: peak.at?.toISOString() ?? null,
        averageConcurrentPlayers:
          servers.server_minutes > 0 ? servers.player_minutes / (hours * 60) : 0,
        playerHours,
      },
      cost: {
        serverCostPerHourInr: this.config.SERVER_COST_PER_HOUR_INR,
        estimatedInr: Math.round(cost * 100) / 100,
        matchesCompleted: matches.completed,
        perMatchInr:
          matches.completed > 0 ? Math.round((cost / matches.completed) * 100) / 100 : null,
        perConcurrentPlayerHourInr:
          playerHours > 0 ? Math.round((cost / playerHours) * 100) / 100 : null,
      },
    };
  }

  /** Retention for raw operations samples. */
  async purge(days: number): Promise<number> {
    const a = await this.db.query(
      `DELETE FROM server_load_samples WHERE minute < now() - make_interval(days => $1::int) RETURNING minute`,
      [days],
    );
    const b = await this.db.query(
      `DELETE FROM api_latency_minutes WHERE minute < now() - make_interval(days => $1::int) RETURNING minute`,
      [days],
    );
    return a.length + b.length;
  }
}
