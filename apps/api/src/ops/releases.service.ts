import { Injectable } from '@nestjs/common';
import { PoolClient } from 'pg';
import { Conflict, NotFound } from '../common/api-error';
import { Db } from '../database/database.module';

interface ReleaseRow {
  id: string;
  platform: 'ANDROID' | 'IOS';
  app_version: string;
  status: string;
  rollout_percent: number;
  regions: string[];
  min_crash_free: string;
  min_sessions: number;
  halt_reason: string | null;
  created_by: string;
  updated_at: Date;
}

/**
 * Staged rollouts (PHASE-PLAN Phase 7 "staged rollout by region/cohort with rollback thresholds"). The store consoles
 * own the actual rollout percentage; this records each release and watches its health from telemetry. When a
 * release's crash-free sessions fall below its threshold after enough sessions, it is HALTED automatically and the
 * on-call follows the rollback runbook (halt the store rollout).
 */
@Injectable()
export class ReleasesService {
  constructor(private readonly db: Db) {}

  async list() {
    const rows = await this.db.query<ReleaseRow & { sessions: number; crashed: number }>(
      `SELECT r.*, h.sessions, h.crashed FROM app_releases r
         LEFT JOIN LATERAL (${HEALTH}) h ON true
        ORDER BY r.created_at DESC LIMIT 50`,
    );
    return { items: rows.map(view) };
  }

  async create(
    input: {
      platform: 'ANDROID' | 'IOS';
      appVersion: string;
      rolloutPercent: number;
      regions: string[];
      minCrashFree: number;
      minSessions: number;
    },
    actor: string,
    audit: (c: PoolClient, id: string, details: object) => Promise<void>,
  ) {
    try {
      return await this.db.tx(async (c) => {
        const { rows } = await c.query<{ id: string }>(
          `INSERT INTO app_releases (platform, app_version, rollout_percent, regions, min_crash_free, min_sessions, created_by)
           VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id`,
          [
            input.platform,
            input.appVersion,
            input.rolloutPercent,
            input.regions,
            input.minCrashFree,
            input.minSessions,
            actor,
          ],
        );
        await audit(c, rows[0]!.id, input);
        return rows[0]!.id;
      });
    } catch (err) {
      if ((err as { code?: string }).code === '23505')
        throw Conflict('RELEASE_EXISTS', 'This version is already recorded');
      throw err;
    }
  }

  async update(
    id: string,
    change: { rolloutPercent?: number; status?: 'ROLLING_OUT' | 'COMPLETE' | 'ROLLED_BACK' },
    audit: (c: PoolClient, before: ReleaseRow) => Promise<void>,
  ) {
    await this.db.tx(async (c) => {
      const { rows } = await c.query<ReleaseRow>(
        'SELECT * FROM app_releases WHERE id = $1 FOR UPDATE',
        [id],
      );
      const r = rows[0];
      if (!r) throw NotFound('Release');
      if (r.status === 'COMPLETE' || r.status === 'ROLLED_BACK')
        throw Conflict('RELEASE_CLOSED', `Release is ${r.status}`);
      await c.query(
        `UPDATE app_releases SET rollout_percent = COALESCE($2, rollout_percent), status = COALESCE($3, status),
           halt_reason = CASE WHEN $3 = 'ROLLING_OUT' THEN NULL ELSE halt_reason END WHERE id = $1`,
        [id, change.rolloutPercent ?? null, change.status ?? null],
      );
      await audit(c, r);
    });
  }

  /** Halts every rolling-out release whose crash-free rate broke its threshold. Returns how many were halted. */
  async evaluate(): Promise<number> {
    const rows = await this.db.query<{
      id: string;
      app_version: string;
      platform: string;
      sessions: number;
      crashed: number;
    }>(
      `UPDATE app_releases r SET status = 'HALTED',
              halt_reason = format('crash-free %s%% < %s%% after %s sessions',
                round(100.0 * (h.sessions - h.crashed) / h.sessions, 2), round(100 * r.min_crash_free, 2), h.sessions)
         FROM app_releases r2 CROSS JOIN LATERAL (${HEALTH.replace(/\br\./g, 'r2.')}) h
        WHERE r.id = r2.id AND r.status = 'ROLLING_OUT' AND h.sessions >= r.min_sessions
          AND (h.sessions - h.crashed)::numeric / h.sessions < r.min_crash_free
        RETURNING r.id, r.app_version, r.platform, h.sessions, h.crashed`,
    );
    for (const r of rows) {
      await this.db.query(
        `INSERT INTO admin_audit_logs (actor, action, entity_type, entity_id, reason, details)
         VALUES ('system', 'release.auto_halt', 'release', $1, 'crash-free sessions below threshold', $2)`,
        [
          r.id,
          {
            platform: r.platform,
            appVersion: r.app_version,
            sessions: r.sessions,
            crashed: r.crashed,
          },
        ],
      );
    }
    return rows.length;
  }
}

/** Sessions and crashed sessions of one release's version and platform, from telemetry (needs alias `r`). */
const HEALTH = `
  SELECT count(DISTINCT t.session_id) FILTER (WHERE t.event_type = 'SESSION_START')::int AS sessions,
         count(DISTINCT t.session_id) FILTER (WHERE t.event_type = 'CRASH')::int AS crashed
    FROM telemetry_events t
   WHERE t.app_version = r.app_version AND t.platform = r.platform AND t.received_at >= r.created_at`;

function view(r: ReleaseRow & { sessions: number | null; crashed: number | null }) {
  const sessions = r.sessions ?? 0;
  return {
    id: r.id,
    platform: r.platform,
    appVersion: r.app_version,
    status: r.status,
    rolloutPercent: r.rollout_percent,
    regions: r.regions,
    minCrashFree: Number(r.min_crash_free),
    minSessions: r.min_sessions,
    haltReason: r.halt_reason,
    health: {
      sessions,
      crashed: r.crashed ?? 0,
      crashFreeRate: sessions > 0 ? (sessions - (r.crashed ?? 0)) / sessions : null,
    },
    createdBy: r.created_by,
    updatedAt: r.updated_at.toISOString(),
  };
}
