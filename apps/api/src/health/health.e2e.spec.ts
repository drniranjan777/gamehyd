import { INestApplication } from '@nestjs/common';
import { Test } from '@nestjs/testing';
import request from 'supertest';
import { configureApp } from '../app.setup';
import { PG_POOL } from '../database/database.module';
import { HealthController } from './health.controller';

function appWithPool(query: jest.Mock): Promise<INestApplication> {
  return Test.createTestingModule({
    controllers: [HealthController],
    providers: [{ provide: PG_POOL, useValue: { query } }],
  })
    .compile()
    .then(async (moduleRef) => {
      const app = moduleRef.createNestApplication();
      configureApp(app);
      await app.init();
      return app;
    });
}

describe('GET /api/v1/health', () => {
  let app: INestApplication;
  afterEach(() => app?.close());

  it('reports liveness without touching the database', async () => {
    const query = jest.fn();
    app = await appWithPool(query);
    const res = await request(app.getHttpServer()).get('/api/v1/health').expect(200);
    expect(res.body).toEqual({ status: 'ok' });
    expect(res.headers['x-request-id']).toBeDefined();
    expect(query).not.toHaveBeenCalled();
  });

  it('reports readiness with the schema version', async () => {
    app = await appWithPool(jest.fn().mockResolvedValue({ rows: [{ version: '0001' }] }));
    const res = await request(app.getHttpServer()).get('/api/v1/health/ready').expect(200);
    expect(res.body).toEqual({ status: 'ok', database: 'ok', schemaVersion: '0001' });
  });

  it('returns the error envelope when the database is down', async () => {
    app = await appWithPool(jest.fn().mockRejectedValue(new Error('ECONNREFUSED')));
    const res = await request(app.getHttpServer())
      .get('/api/v1/health/ready')
      .set('x-request-id', 'test-req-0001')
      .expect(503);
    expect(res.body).toEqual({
      error: {
        code: 'SERVICE_UNAVAILABLE',
        message: 'Database is not reachable or not migrated',
        requestId: 'test-req-0001',
        details: [],
      },
    });
    expect(JSON.stringify(res.body)).not.toContain('ECONNREFUSED');
  });

  it('returns a 404 envelope for unknown routes', async () => {
    app = await appWithPool(jest.fn());
    const res = await request(app.getHttpServer()).get('/api/v1/nope').expect(404);
    expect(res.body.error.code).toBe('NOT_FOUND');
  });
});
