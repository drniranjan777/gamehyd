import { INestApplication } from '@nestjs/common';
import type { NestExpressApplication } from '@nestjs/platform-express';
import { accessLog } from './common/access-log.middleware';
import { LatencyRecorder } from './ops/latency.recorder';
import { ErrorEnvelopeFilter } from './common/error-envelope.filter';
import { requestId } from './common/request-id.middleware';
import type { AppConfig } from './config';
import type { NextFunction, Request, Response } from 'express';

/** JSON-only API: nothing may be framed, sniffed, cached by intermediaries, or run as a document. */
function securityHeaders(_req: Request, res: Response, next: NextFunction): void {
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('X-Frame-Options', 'DENY');
  res.setHeader('Referrer-Policy', 'no-referrer');
  res.setHeader('Content-Security-Policy', "default-src 'none'; frame-ancestors 'none'");
  res.setHeader('Strict-Transport-Security', 'max-age=31536000; includeSubDomains');
  res.setHeader('Cache-Control', 'no-store');
  next();
}

/**
 * Shared by main.ts and the e2e tests so both run the same pipeline.
 * The app must be created with `{ rawBody: true }` so /internal signatures can be checked over the exact bytes.
 */
export function configureApp(
  app: INestApplication,
  config?: Pick<AppConfig, 'LOG_REQUESTS' | 'TRUST_PROXY'>,
): void {
  app.setGlobalPrefix('api/v1');
  if (config?.TRUST_PROXY) (app as NestExpressApplication).set('trust proxy', 1);
  (app as NestExpressApplication).disable('x-powered-by');
  app.use(securityHeaders);
  // Latency histograms per route for the ops dashboard (absent in unit setups without the full module).
  try {
    app.use(app.get(LatencyRecorder).middleware);
  } catch {
    // LatencyRecorder not provided
  }
  app.use(requestId);
  app.use(accessLog(config?.LOG_REQUESTS ?? false));
  app.useGlobalFilters(new ErrorEnvelopeFilter());
}
