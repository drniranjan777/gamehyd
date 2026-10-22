import { Inject, Injectable, Logger, OnApplicationShutdown, OnModuleInit } from '@nestjs/common';
import { APP_CONFIG, AppConfig } from '../config';
import { Db } from '../database/database.module';
import { StoreService } from './store.service';

export interface ReconcileReport {
  cancelledPendingOrders: number;
  walletsWithLedgerDrift: number;
  unmatchedPurchaseEvents: number;
  accountsDeleted: number;
  suspensionsLifted: number;
}

/**
 * Periodic housekeeping (BRD §9 "reconciliation"; §13 account deletion). Runs in-process every
 * RECONCILE_INTERVAL_SECONDS (0 = only on demand from the admin API). With several API instances each runs it;
 * every step is idempotent and row-locked, so that is safe.
 */
@Injectable()
export class JobsService implements OnModuleInit, OnApplicationShutdown {
  private readonly log = new Logger('Jobs');
  private timer?: NodeJS.Timeout;
  private running = false;

  constructor(
    private readonly db: Db,
    private readonly store: StoreService,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {}

  onModuleInit(): void {
    const seconds = this.config.RECONCILE_INTERVAL_SECONDS;
    if (seconds > 0) {
      this.timer = setInterval(
        () => void this.reconcile().catch((e: Error) => this.log.error(e.message)),
        seconds * 1000,
      );
      this.timer.unref();
    }
  }

  onApplicationShutdown(): void {
    if (this.timer) clearInterval(this.timer);
  }

  async reconcile(): Promise<ReconcileReport> {
    if (this.running) throw new Error('reconciliation already running');
    this.running = true;
    try {
      const cancelledPendingOrders = await this.store.cancelStalePending(
        this.config.ORDER_PENDING_TTL_HOURS,
      );
      const drift = await this.db.one<{ n: number }>(
        'SELECT count(*)::int AS n FROM wallet_ledger_drift',
      );
      const unmatched = await this.db.one<{ n: number }>(
        `SELECT count(*)::int AS n FROM purchase_events WHERE outcome = 'UNMATCHED' AND received_at > now() - interval '7 days'`,
      );
      const accountsDeleted = await this.processDeletions();
      const suspensionsLifted = await this.liftExpiredSuspensions();
      const report = {
        suspensionsLifted,
        cancelledPendingOrders,
        walletsWithLedgerDrift: drift!.n,
        unmatchedPurchaseEvents: unmatched!.n,
        accountsDeleted,
      };
      if (report.walletsWithLedgerDrift > 0)
        this.log.error(`ledger drift in ${report.walletsWithLedgerDrift} wallet(s)`);
      return report;
    } finally {
      this.running = false;
    }
  }

  /** Ends suspensions whose time is up (the latest sanction is a SUSPEND that has expired). */
  async liftExpiredSuspensions(): Promise<number> {
    const rows = await this.db.query<{ id: string }>(
      `UPDATE users u SET status = 'ACTIVE'
        WHERE u.status = 'SUSPENDED'
          AND (SELECT m.expires_at FROM moderation_actions m
                WHERE m.user_id = u.id AND m.action IN ('SUSPEND', 'BAN', 'UNBAN')
                ORDER BY m.created_at DESC LIMIT 1) <= now()
        RETURNING u.id`,
    );
    for (const r of rows)
      await this.db.query(
        `INSERT INTO admin_audit_logs (actor, action, entity_type, entity_id, reason)
         VALUES ('system', 'player.suspension_ended', 'user', $1, 'suspension expired')`,
        [r.id],
      );
    return rows.length;
  }

  /**
   * Erases accounts whose grace period has ended: sign-in data, sessions, inventory and the display name go;
   * orders, payments and the wallet ledger stay (financial records, BRD §13 retention) against the anonymous id.
   */
  async processDeletions(): Promise<number> {
    const due = await this.db.query<{ user_id: string }>(
      `SELECT user_id FROM account_deletion_requests
        WHERE completed_at IS NULL AND cancelled_at IS NULL AND scheduled_for <= now() LIMIT 100`,
    );
    let done = 0;
    for (const { user_id } of due) {
      const erased = await this.db.tx(async (c) => {
        const { rows } = await c.query<{ status: string }>(
          'SELECT status FROM users WHERE id = $1 FOR UPDATE',
          [user_id],
        );
        if (rows[0]?.status !== 'PENDING_DELETION') return false;
        // Sign-in data is overwritten rather than deleted: the app role cannot delete identities or sessions.
        await c.query(
          `UPDATE auth_identities SET provider_subject = 'deleted:' || id, credential_hash = CASE WHEN provider = 'GUEST' THEN sha256(gen_random_uuid()::text::bytea) END,
             last_used_at = NULL WHERE user_id = $1`,
          [user_id],
        );
        await c.query(
          `UPDATE player_sessions SET revoked_at = now(), revoke_reason = 'ACCOUNT_DELETED'
            WHERE user_id = $1 AND revoked_at IS NULL`,
          [user_id],
        );
        for (const table of ['matchmaking_queue', 'loadout_slots', 'player_inventory'])
          await c.query(`DELETE FROM ${table} WHERE user_id = $1`, [user_id]);
        await c.query(
          `UPDATE player_profiles SET display_name = 'Deleted player' WHERE user_id = $1`,
          [user_id],
        );
        await c.query(`UPDATE users SET status = 'DELETED', deleted_at = now() WHERE id = $1`, [
          user_id,
        ]);
        await c.query(
          'UPDATE account_deletion_requests SET completed_at = now() WHERE user_id = $1',
          [user_id],
        );
        await c.query(
          `INSERT INTO admin_audit_logs (actor, action, entity_type, entity_id, reason)
           VALUES ('system', 'account.deleted', 'user', $1, 'deletion grace period ended')`,
          [user_id],
        );
        return true;
      });
      if (erased) done++;
    }
    return done;
  }
}
