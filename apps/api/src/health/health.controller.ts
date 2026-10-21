import { Controller, Get, Inject, ServiceUnavailableException } from '@nestjs/common';
import { Pool } from 'pg';
import { PG_POOL } from '../database/database.module';

@Controller('health')
export class HealthController {
  constructor(@Inject(PG_POOL) private readonly pool: Pool) {}

  /** Liveness: the process is up. Never touches dependencies. */
  @Get()
  live(): { status: 'ok' } {
    return { status: 'ok' };
  }

  /** Readiness: the database answers and migrations have been applied. */
  @Get('ready')
  async ready(): Promise<{ status: 'ok'; database: 'ok'; schemaVersion: string | null }> {
    try {
      const { rows } = await this.pool.query<{ version: string | null }>(
        'SELECT max(version) AS version FROM schema_migrations',
      );
      return { status: 'ok', database: 'ok', schemaVersion: rows[0]?.version ?? null };
    } catch {
      throw new ServiceUnavailableException('Database is not reachable or not migrated');
    }
  }
}
