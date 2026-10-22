import { Injectable } from '@nestjs/common';
import { Db } from '../database/database.module';

/**
 * Economy review (PHASE-PLAN Phase 7, BRD §12): where currency comes from and where it goes, how much is in
 * circulation and what sells. Read from the ledger, so it always agrees with player balances.
 */
@Injectable()
export class EconomyService {
  constructor(private readonly db: Db) {}

  async report(days: number) {
    const since = `now() - make_interval(days => $1::int)`;
    const flows = await this.db.query<{
      currency: 'COIN' | 'GEM';
      reason: string;
      issued: string;
      removed: string;
      entries: number;
    }>(
      `SELECT w.currency_code AS currency, t.reason,
              COALESCE(sum(t.amount) FILTER (WHERE t.amount > 0), 0)::bigint AS issued,
              COALESCE(-sum(t.amount) FILTER (WHERE t.amount < 0), 0)::bigint AS removed,
              count(*)::int AS entries
         FROM wallet_transactions t JOIN wallets w ON w.id = t.wallet_id
        WHERE t.created_at > ${since}
        GROUP BY 1, 2 ORDER BY 1, 2`,
      [days],
    );
    const supply = await this.db.query<{
      currency: 'COIN' | 'GEM';
      total: string;
      holders: number;
      median: number | null;
      p90: number | null;
    }>(
      `SELECT w.currency_code AS currency, sum(w.balance)::bigint AS total, count(*) FILTER (WHERE w.balance > 0)::int AS holders,
              percentile_cont(0.5) WITHIN GROUP (ORDER BY w.balance)::float AS median,
              percentile_cont(0.9) WITHIN GROUP (ORDER BY w.balance)::float AS p90
         FROM wallets w JOIN users u ON u.id = w.user_id WHERE u.status <> 'DELETED' GROUP BY 1 ORDER BY 1`,
    );
    const sales = await this.db.query<{
      product_key: string;
      currency: string;
      platform_group: string;
      orders: number;
      refunds: number;
      gross: string;
    }>(
      `SELECT p.key AS product_key, o.currency_code AS currency,
              CASE WHEN o.platform = 'IN_GAME' THEN 'IN_GAME' WHEN o.platform = 'DEV' THEN 'DEV' ELSE 'STORE' END AS platform_group,
              count(*) FILTER (WHERE o.status IN ('FULFILLED', 'REFUNDED'))::int AS orders,
              count(*) FILTER (WHERE o.status = 'REFUNDED')::int AS refunds,
              COALESCE(sum(o.amount_minor) FILTER (WHERE o.status = 'FULFILLED'), 0)::bigint AS gross
         FROM orders o JOIN store_products p ON p.id = o.product_id
        WHERE o.created_at > ${since}
        GROUP BY 1, 2, 3 ORDER BY orders DESC, 1 LIMIT 50`,
      [days],
    );
    const seasons = await this.db.query<{
      key: string;
      players: number;
      premium: number;
      claims: number;
    }>(
      `SELECT s.key, count(sp.user_id)::int AS players, count(sp.user_id) FILTER (WHERE sp.premium)::int AS premium,
              (SELECT count(*)::int FROM season_claims c WHERE c.season_id = s.id) AS claims
         FROM seasons s LEFT JOIN season_progress sp ON sp.season_id = s.id
        WHERE s.status = 'PUBLISHED' AND s.ends_at > ${since}
        GROUP BY s.id, s.key ORDER BY s.key`,
      [days],
    );

    const byCurrency = (c: 'COIN' | 'GEM') => {
      const f = flows.filter((x) => x.currency === c);
      const issued = f.reduce((a, x) => a + Number(x.issued), 0);
      const removed = f.reduce((a, x) => a + Number(x.removed), 0);
      const s = supply.find((x) => x.currency === c);
      return {
        issued,
        removed,
        net: issued - removed,
        sinkRatio: issued > 0 ? removed / issued : null,
        circulating: Number(s?.total ?? 0),
        holders: s?.holders ?? 0,
        medianBalance: s?.median ?? 0,
        p90Balance: s?.p90 ?? 0,
        byReason: f.map((x) => ({
          reason: x.reason,
          issued: Number(x.issued),
          removed: Number(x.removed),
          entries: x.entries,
        })),
      };
    };

    return {
      days,
      generatedAt: new Date().toISOString(),
      currencies: { COIN: byCurrency('COIN'), GEM: byCurrency('GEM') },
      sales: sales.map((s) => ({
        productKey: s.product_key,
        currency: s.currency,
        channel: s.platform_group,
        orders: s.orders,
        refunds: s.refunds,
        grossMinor: Number(s.gross),
      })),
      seasons: seasons.map((s) => ({
        seasonKey: s.key,
        players: s.players,
        premiumPlayers: s.premium,
        rewardsClaimed: s.claims,
      })),
    };
  }
}
