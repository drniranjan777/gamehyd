import { INestApplication } from '@nestjs/common';
import type { NestExpressApplication } from '@nestjs/platform-express';
import { accessLog } from './common/access-log.middleware';
import { ErrorEnvelopeFilter } from './common/error-envelope.filter';
import { requestId } from './common/request-id.middleware';
import type { AppConfig } from './config';

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
  app.use(requestId);
  app.use(accessLog(config?.LOG_REQUESTS ?? false));
  app.useGlobalFilters(new ErrorEnvelopeFilter());
}
