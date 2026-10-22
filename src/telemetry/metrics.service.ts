import { Injectable } from '@nestjs/common';
import { Db } from '../database/database.module';

type GateStatus = 'PASS' | 'FAIL' | 'NO_DATA' | 'MANUAL';

const ratio = (num: number, den: number) => (den > 0 ? num / den : null);

/**
 * The Phase 6 beta KPIs and the BRD §14 release gates, computed from the database over the last `days` days.
 * Rates are fractions (0..1) or null when there is no data yet.
 */
@Injectable()
export class MetricsService {
  constructor(private readonly db: Db) {}

  async compute(days: number) {
    const p = [days];
    const since = `now() - make_interval(days => $1::int)`;

    const sessions = (await this.db.one<{ sessions: number; crashed: number }>(
      `SELECT count(DISTINCT session_id) FILTER (WHERE event_type = 'SESSION_START')::int AS sessions,
              count(DISTINCT session_id) FILTER (WHERE event_type = 'CRASH'
                AND session_id IN (SELECT session_id FROM telemetry_events WHERE event_type = 'SESSION_START' AND received_at > ${since}))::int AS crashed
         FROM telemetry_events WHERE received_at > ${since}`,
      p,
    ))!;

    const fps = await this.db.query<{
      platform: string;
      device_model: string;
      samples: number;
      avg_fps: number;
      p5_fps: number;
    }>(
      `SELECT platform, device_model, count(*)::int AS samples,
              round((sum((data->>'avgFps')::numeric * (data->>'seconds')::numeric) / sum((data->>'seconds')::numeric)), 1)::float AS avg_fps,
              round(avg((data->>'p5Fps')::numeric), 1)::float AS p5_fps
         FROM telemetry_events WHERE event_type = 'FPS_SAMPLE' AND received_at > ${since}
        GROUP BY platform, device_model ORDER BY samples DESC, device_model LIMIT 20`,
      p,
    );

    const matches = (await this.db.one<{ hosted: number; completed: number }>(
      `SELECT count(*) FILTER (WHERE status IN ('COMPLETED', 'TERMINATED', 'CANCELLED'))::int AS hosted,
              count(*) FILTER (WHERE status = 'COMPLETED')::int AS completed
         FROM matches WHERE hosted_at IS NOT NULL AND created_at > ${since}`,
      p,
    ))!;

    const play = (await this.db.one<{
      participations: number;
      disconnects: number;
      reconnected: number;
    }>(
      `SELECT (SELECT count(*)::int FROM match_participants WHERE assigned_at > ${since}) AS participations,
              count(*)::int AS disconnects,
              count(*) FILTER (WHERE (data->>'reconnected')::boolean)::int AS reconnected
         FROM telemetry_events WHERE event_type = 'DISCONNECT' AND received_at > ${since}`,
      p,
    ))!;

    const queue = (await this.db.one<{ n: number; p50: number | null; p95: number | null }>(
      `SELECT count(*)::int AS n,
              percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM assigned_at - enqueued_at))::float AS p50,
              percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM assigned_at - enqueued_at))::float AS p95
         FROM match_participants WHERE enqueued_at IS NOT NULL AND assigned_at > ${since}`,
      p,
    ))!;

    // Day-N retention: of players who signed up in the window (and are at least N+1 days old), the share active on
    // day N after sign-up. Activity is a sign-in or token refresh (player_sessions) or a telemetry session.
    const retention = async (n: number) => {
      const r = (await this.db.one<{ cohort: number; retained: number }>(
        `WITH cohort AS (
           SELECT id, created_at FROM users
            WHERE created_at > now() - make_interval(days => $1::int + $2::int)
              AND created_at <= now() - make_interval(days => $2::int + 1))
         SELECT count(*)::int AS cohort,
                count(*) FILTER (WHERE EXISTS (
                  SELECT 1 FROM player_sessions s WHERE s.user_id = c.id
                     AND s.created_at >= c.created_at + make_interval(days => $2::int)
                     AND s.created_at < c.created_at + make_interval(days => $2::int + 1))
                  OR EXISTS (
                  SELECT 1 FROM telemetry_events t WHERE t.user_id = c.id AND t.event_type = 'SESSION_START'
                     AND t.client_time >= c.created_at + make_interval(days => $2::int)
                     AND t.client_time < c.created_at + make_interval(days => $2::int + 1)))::int AS retained
           FROM cohort c`,
        [days, n],
      ))!;
      return { cohort: r.cohort, retained: r.retained, rate: ratio(r.retained, r.cohort) };
    };

    const economy = (await this.db.one<{
      active: number;
      payers: number;
      spenders: number;
      duplicates: number;
      unpaid: number;
      drift: number;
    }>(
      `SELECT (SELECT count(DISTINCT user_id)::int FROM player_sessions WHERE created_at > ${since}) AS active,
              (SELECT count(DISTINCT user_id)::int FROM orders
                WHERE platform IN ('ANDROID', 'IOS') AND status IN ('FULFILLED', 'REFUNDED') AND fulfilled_at > ${since}) AS payers,
              (SELECT count(DISTINCT user_id)::int FROM orders
                WHERE platform = 'IN_GAME' AND status = 'FULFILLED' AND fulfilled_at > ${since}) AS spenders,
              (SELECT count(*)::int FROM (SELECT 1 FROM payment_transactions
                GROUP BY platform, provider_transaction_id HAVING count(*) > 1) d) AS duplicates,
              (SELECT count(*)::int FROM orders o WHERE o.platform <> 'IN_GAME' AND o.status IN ('FULFILLED', 'REFUNDED')
                AND NOT EXISTS (SELECT 1 FROM payment_transactions t WHERE t.order_id = o.id)) AS unpaid,
              (SELECT count(*)::int FROM wallet_ledger_drift) AS drift`,
      p,
    ))!;

    const crashFree = sessions.sessions > 0 ? 1 - sessions.crashed / sessions.sessions : null;
    const completion = ratio(matches.completed, matches.hosted);
    const gate = (value: number | null, pass: (v: number) => boolean): GateStatus =>
      value === null ? 'NO_DATA' : pass(value) ? 'PASS' : 'FAIL';
    const integrity = economy.duplicates + economy.unpaid + economy.drift;

    return {
      days,
      generatedAt: new Date().toISOString(),
      sessions: { total: sessions.sessions, crashed: sessions.crashed, crashFreeRate: crashFree },
      fpsByDevice: fps.map((f) => ({
        platform: f.platform,
        deviceModel: f.device_model,
        samples: f.samples,
        avgFps: f.avg_fps,
        p5Fps: f.p5_fps,
      })),
      matches: { hosted: matches.hosted, completed: matches.completed, completionRate: completion },
      disconnects: {
        total: play.disconnects,
        reconnected: play.reconnected,
        per100Participations:
          play.participations > 0 ? (play.disconnects * 100) / play.participations : null,
      },
      queueSeconds: { samples: queue.n, p50: queue.p50, p95: queue.p95 },
      retention: { d1: await retention(1), d7: await retention(7) },
      economy: {
        activePlayers: economy.active,
        payers: economy.payers,
        payerConversion: ratio(economy.payers, economy.active),
        inGameSpenders: economy.spenders,
      },
      purchaseIntegrity: {
        duplicateTransactions: economy.duplicates,
        paidOrdersWithoutPayment: economy.unpaid,
        walletsWithLedgerDrift: economy.drift,
      },
      releaseGates: [
        {
          key: 'crash_free_sessions',
          target: '>= 99.5% of sessions',
          value: crashFree,
          status: gate(crashFree, (v) => v >= 0.995),
        },
        {
          key: 'match_completion',
          target: '>= 95% of hosted matches',
          value: completion,
          status: gate(completion, (v) => v >= 0.95),
        },
        {
          key: 'purchase_duplication',
          target: '0 duplicate or unpaid grants, 0 ledger drift',
          value: integrity,
          status: (integrity === 0 ? 'PASS' : 'FAIL') as GateStatus,
        },
        {
          key: 'security_issues',
          target: 'no open critical/high findings',
          value: null,
          status: 'MANUAL' as GateStatus,
        },
        {
          key: 'legal_review',
          target: 'privacy, terms, retention approved',
          value: null,
          status: 'MANUAL' as GateStatus,
        },
        {
          key: 'on_call',
          target: 'rotation staffed, runbooks rehearsed',
          value: null,
          status: 'MANUAL' as GateStatus,
        },
      ],
    };
  }

  /** Telemetry retention (privacy): raw events older than `days` are deleted. */
  async purgeTelemetry(days: number): Promise<number> {
    const rows = await this.db.query(
      `DELETE FROM telemetry_events WHERE received_at < now() - make_interval(days => $1::int) RETURNING id`,
      [days],
    );
    return rows.length;
  }
}
