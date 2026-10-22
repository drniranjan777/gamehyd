import { Global, Inject, Injectable, Module, OnApplicationShutdown } from '@nestjs/common';
import { Pool, PoolClient, QueryResultRow } from 'pg';
import { APP_CONFIG, AppConfig } from '../config';

export const PG_POOL = Symbol('PG_POOL');

/** Thin wrapper over the pool: single queries and serialisable-enough transactions. */
@Injectable()
export class Db {
  constructor(@Inject(PG_POOL) private readonly pool: Pool) {}

  async query<T extends QueryResultRow>(text: string, params: unknown[] = []): Promise<T[]> {
    const { rows } = await this.pool.query<T>(text, params);
    return rows;
  }

  async one<T extends QueryResultRow>(
    text: string,
    params: unknown[] = [],
  ): Promise<T | undefined> {
    return (await this.query<T>(text, params))[0];
  }

  /** Runs fn in a transaction; rolls back on any error. */
  async tx<T>(fn: (c: PoolClient) => Promise<T>): Promise<T> {
    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');
      const result = await fn(client);
      await client.query('COMMIT');
      return result;
    } catch (err) {
      await client.query('ROLLBACK').catch(() => undefined);
      throw err;
    } finally {
      client.release();
    }
  }
}

@Global()
@Module({
  providers: [
    {
      provide: PG_POOL,
      inject: [APP_CONFIG],
      useFactory: (config: AppConfig) =>
        new Pool({
          connectionString: config.DATABASE_URL,
          max: 10,
          statement_timeout: 5_000,
          connectionTimeoutMillis: 2_000,
        }),
    },
    Db,
  ],
  exports: [PG_POOL, Db],
})
export class DatabaseModule implements OnApplicationShutdown {
  constructor(@Inject(PG_POOL) private readonly pool: Pool) {}

  async onApplicationShutdown(): Promise<void> {
    await this.pool.end();
  }
}
