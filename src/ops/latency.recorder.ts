import { Inject, Injectable, Logger, OnApplicationShutdown, OnModuleInit } from '@nestjs/common';
import type { NextFunction, Request, Response } from 'express';
import { hostname } from 'node:os';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';

/** Upper bounds (ms) of the latency histogram buckets; the last bucket is everything slower. */
export const LATENCY_BUCKETS_MS = [
  5, 10, 25, 50, 75, 100, 150, 200, 300, 500, 750, 1000, 2000, 5000,
];

interface RouteStats {
  requests: number;
  serverErrors: number;
  buckets: number[];
}

/** Percentile from summed histogram buckets: the upper bound of the bucket holding the p-th request. */
export function percentileFromBuckets(buckets: number[], p: number): number | null {
  const total = buckets.reduce((a, b) => a + b, 0);
  if (total === 0) return null;
  const rank = Math.ceil((p / 100) * total);
  let seen = 0;
  for (let i = 0; i < buckets.length; i++) {
    seen += buckets[i]!;
    if (seen >= rank)
      return LATENCY_BUCKETS_MS[i] ?? LATENCY_BUCKETS_MS[LATENCY_BUCKETS_MS.length - 1]! * 2;
  }
  return null;
}

/**
 * Measures every API request (by route template, e.g. /api/v1/store/orders/:orderId) into a histogram and writes
 * one row per route per minute to api_latency_minutes, so the ops dashboard can show p50/p95/p99 across instances.
 */
@Injectable()
export class LatencyRecorder implements OnModuleInit, OnApplicationShutdown {
  private readonly log = new Logger('Latency');
  private readonly instance = `${hostname()}:${process.pid}`.slice(0, 64);
  private window = new Map<string, RouteStats>();
  private windowStart = new Date();
  private timer?: NodeJS.Timeout;

  constructor(
    private readonly db: Db,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {}

  onModuleInit(): void {
    if (this.config.OPS_FLUSH_SECONDS > 0) {
      this.timer = setInterval(
        () => void this.flush().catch((e: Error) => this.log.warn(e.message)),
        this.config.OPS_FLUSH_SECONDS * 1000,
      );
      this.timer.unref();
    }
  }

  async onApplicationShutdown(): Promise<void> {
    if (this.timer) clearInterval(this.timer);
    await this.flush().catch(() => undefined);
  }

  /** Express middleware; register before the routes. */
  middleware = (req: Request, res: Response, next: NextFunction): void => {
    const start = process.hrtime.bigint();
    res.on('finish', () => {
      const ms = Number(process.hrtime.bigint() - start) / 1e6;
      const route = req.route?.path
        ? `${req.method} ${req.baseUrl}${req.route.path}`
        : `${req.method} (unmatched)`;
      this.record(route.slice(0, 120), ms, res.statusCode);
    });
    next();
  };

  record(route: string, ms: number, status: number): void {
    let s = this.window.get(route);
    if (!s) {
      s = {
        requests: 0,
        serverErrors: 0,
        buckets: new Array(LATENCY_BUCKETS_MS.length + 1).fill(0),
      };
      this.window.set(route, s);
    }
    s.requests++;
    if (status >= 500) s.serverErrors++;
    const i = LATENCY_BUCKETS_MS.findIndex((b) => ms <= b);
    s.buckets[i < 0 ? LATENCY_BUCKETS_MS.length : i]!++;
  }

  /** Writes the current window (attributed to the minute it started in) and starts a new one. */
  async flush(): Promise<number> {
    const rows = [...this.window.entries()];
    const minute = new Date(Math.floor(this.windowStart.getTime() / 60_000) * 60_000);
    this.window = new Map();
    this.windowStart = new Date();
    if (rows.length === 0) return 0;
    await this.db.query(
      `INSERT INTO api_latency_minutes (minute, instance, route, requests, server_errors, buckets)
       SELECT $1, $2, r.route, r.requests, r.errors, r.buckets
         FROM jsonb_to_recordset($3::jsonb) AS r(route text, requests int, errors int, buckets int[])
       ON CONFLICT (minute, instance, route) DO UPDATE SET
         requests = api_latency_minutes.requests + EXCLUDED.requests,
         server_errors = api_latency_minutes.server_errors + EXCLUDED.server_errors,
         buckets = (SELECT array_agg(a + b ORDER BY i) FROM unnest(api_latency_minutes.buckets, EXCLUDED.buckets)
                      WITH ORDINALITY AS t(a, b, i))`,
      [
        minute,
        this.instance,
        JSON.stringify(
          rows.map(([route, s]) => ({
            route,
            requests: s.requests,
            errors: s.serverErrors,
            buckets: s.buckets,
          })),
        ),
      ],
    );
    return rows.length;
  }
}
