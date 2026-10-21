import { INestApplication } from '@nestjs/common';
import { ErrorEnvelopeFilter } from './common/error-envelope.filter';
import { requestId } from './common/request-id.middleware';

/** Shared by main.ts and the e2e tests so both run the same pipeline. */
export function configureApp(app: INestApplication): void {
  app.setGlobalPrefix('api/v1');
  app.use(requestId);
  app.useGlobalFilters(new ErrorEnvelopeFilter());
}
