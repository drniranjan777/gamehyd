import { api, type Schemas } from '../../lib/api';
import { money } from '../../lib/format';

export default async function OverviewPage() {
  const o = await api<Schemas['AdminOverview']>('/admin/overview');
  const tiles: [string, string | number, boolean?][] = [
    ['Players', o.players],
    ['Active players (24 h)', o.activePlayers24h],
    ['Matches (24 h)', o.matches24h],
    ['Live matches', o.liveMatches],
    ['Servers online', o.serversOnline, o.serversOnline === 0],
    ['Open reports', o.openReports, o.openReports > 0],
    ['Products awaiting approval', o.productsAwaitingApproval, o.productsAwaitingApproval > 0],
    ['Pending orders', o.pendingOrders],
    ['Revenue, 30 days (gross)', money({ currency: 'INR', amountMinor: o.revenueInrMinor30d })],
    ['Refunds, 30 days', o.refunds30d],
    ['Wallets with ledger drift', o.walletsWithLedgerDrift, o.walletsWithLedgerDrift > 0],
  ];
  return (
    <>
      <h1>Overview</h1>
      <div className="tiles">
        {tiles.map(([label, value, alert]) => (
          <div key={label} className={alert ? 'tile alert' : 'tile'}>
            <div className="value">{value}</div>
            <div className="label">{label}</div>
          </div>
        ))}
      </div>
      <p className="muted">
        Revenue counts fulfilled Google Play and App Store orders before platform fees and refunds;
        dev purchases are excluded. Ledger drift should always be zero.
      </p>
    </>
  );
}
