import Link from 'next/link';
import { api, qs, type Schemas } from '../../../lib/api';
import { money, when } from '../../../lib/format';

export default async function EconomyPage({
  searchParams,
}: {
  searchParams: Promise<{ days?: string }>;
}) {
  const { days = '30' } = await searchParams;
  const e = await api<Schemas['EconomyReport']>(`/admin/economy${qs({ days })}`);
  return (
    <>
      <h1>Economy review</h1>
      <p>
        {[7, 30, 90].map((d) => (
          <Link
            key={d}
            href={`/economy?days=${d}`}
            style={{ marginRight: 12, fontWeight: String(d) === days ? 700 : 400 }}
          >
            last {d} days
          </Link>
        ))}
        <span className="muted"> · generated {when(e.generatedAt)}</span>
      </p>
      <div className="row">
        {(['COIN', 'GEM'] as const).map((c) => {
          const f = e.currencies[c];
          return (
            <div key={c} className="panel">
              <h2 style={{ marginTop: 0 }}>{c === 'COIN' ? 'Coins' : 'Gems'}</h2>
              <dl className="kv">
                <dt>Issued / removed</dt>
                <dd>
                  {f.issued.toLocaleString('en-IN')} / {f.removed.toLocaleString('en-IN')} (net{' '}
                  {f.net.toLocaleString('en-IN')})
                </dd>
                <dt>Sink ratio</dt>
                <dd>
                  {f.sinkRatio === null ? '—' : `${Math.round(f.sinkRatio * 100)}% of issued spent`}
                </dd>
                <dt>In circulation</dt>
                <dd>
                  {f.circulating.toLocaleString('en-IN')} across {f.holders} holders
                </dd>
                <dt>Balance median / p90</dt>
                <dd>
                  {f.medianBalance} / {f.p90Balance}
                </dd>
              </dl>
              <table style={{ marginTop: 12 }}>
                <thead>
                  <tr>
                    <th>Reason</th>
                    <th>Issued</th>
                    <th>Removed</th>
                  </tr>
                </thead>
                <tbody>
                  {f.byReason.map((r) => (
                    <tr key={r.reason}>
                      <td>{r.reason}</td>
                      <td>{r.issued}</td>
                      <td>{r.removed}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          );
        })}
      </div>
      <h2>Sales</h2>
      <table>
        <thead>
          <tr>
            <th>Product</th>
            <th>Channel</th>
            <th>Orders</th>
            <th>Refunds</th>
            <th>Gross (fulfilled)</th>
          </tr>
        </thead>
        <tbody>
          {e.sales.map((s) => (
            <tr key={`${s.productKey}-${s.channel}`}>
              <td>{s.productKey}</td>
              <td>{s.channel}</td>
              <td>{s.orders}</td>
              <td>{s.refunds}</td>
              <td>{money({ currency: s.currency, amountMinor: s.grossMinor || 0 })}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <h2>Seasons</h2>
      <table>
        <thead>
          <tr>
            <th>Season</th>
            <th>Players with progress</th>
            <th>Pass holders</th>
            <th>Rewards claimed</th>
          </tr>
        </thead>
        <tbody>
          {e.seasons.map((s) => (
            <tr key={s.seasonKey}>
              <td>{s.seasonKey}</td>
              <td>{s.players}</td>
              <td>{s.premiumPlayers}</td>
              <td>{s.rewardsClaimed}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="muted">
        Healthy coin economy: a sink ratio well above zero (players spend what they earn) and a
        median balance that doesn't keep climbing season over season. Coins are never sold for money
        (BRD §12).
      </p>
    </>
  );
}
