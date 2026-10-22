import Link from 'next/link';
import { api, qs, type Schemas } from '../../../lib/api';
import { when } from '../../../lib/format';

const ms = (v: number | null | undefined) => (v === null || v === undefined ? '—' : `≤ ${v} ms`);
const inr = (v: number | null | undefined) =>
  v === null || v === undefined ? '—' : `₹${v.toFixed(2)}`;

export default async function OpsPage({
  searchParams,
}: {
  searchParams: Promise<{ hours?: string }>;
}) {
  const { hours = '24' } = await searchParams;
  const o = await api<Schemas['OpsDashboard']>(`/admin/ops${qs({ hours })}`);
  const tiles: [string, string, boolean?][] = [
    ['API p95', ms(o.api.p95Ms), (o.api.p95Ms ?? 0) > 300],
    ['API p99', ms(o.api.p99Ms)],
    [
      'API 5xx rate',
      o.api.serverErrorRate === null ? '—' : `${(o.api.serverErrorRate * 100).toFixed(2)}%`,
      (o.api.serverErrorRate ?? 0) > 0.001,
    ],
    [
      'Server tick p99 (median / worst)',
      `${o.servers.tickP99Median?.toFixed(1) ?? '—'} / ${o.servers.tickP99Worst?.toFixed(1) ?? '—'} ms`,
      (o.servers.tickP99Worst ?? 0) > o.servers.tickBudgetMs,
    ],
    [
      'Minutes over tick budget',
      String(o.servers.minutesOverBudget),
      o.servers.minutesOverBudget > 0,
    ],
    ['Peak concurrent players', String(o.capacity.peakConcurrentPlayers)],
    ['Server-hours', o.servers.serverHours.toFixed(1)],
    ['Cost per match', inr(o.cost.perMatchInr)],
    ['Cost per concurrent player-hour', inr(o.cost.perConcurrentPlayerHourInr)],
  ];
  return (
    <>
      <h1>Operations</h1>
      <p>
        {[1, 24, 168].map((h) => (
          <Link
            key={h}
            href={`/ops?hours=${h}`}
            style={{ marginRight: 12, fontWeight: String(h) === hours ? 700 : 400 }}
          >
            last {h === 168 ? '7 days' : `${h} h`}
          </Link>
        ))}
        <span className="muted"> · generated {when(o.generatedAt)}</span>
      </p>
      <div className="tiles">
        {tiles.map(([label, value, alert]) => (
          <div key={label} className={alert ? 'tile alert' : 'tile'}>
            <div className="value" style={{ fontSize: 20 }}>
              {value}
            </div>
            <div className="label">{label}</div>
          </div>
        ))}
      </div>
      <p className="muted">
        {o.api.requests.toLocaleString('en-IN')} API requests. Latencies are histogram bucket upper
        bounds. Tick budget {o.servers.tickBudgetMs} ms; servers were hosting{' '}
        {o.servers.hostingShare === null ? '—' : `${Math.round(o.servers.hostingShare * 100)}%`} of
        the time. Cost uses ₹{o.cost.serverCostPerHourInr}/server-hour ({inr(o.cost.estimatedInr)}{' '}
        for {o.cost.matchesCompleted} completed matches); set SERVER_COST_PER_HOUR_INR from the
        chosen cloud instance.
      </p>
      <h2>Slowest routes by traffic</h2>
      <table>
        <thead>
          <tr>
            <th>Route</th>
            <th>Requests</th>
            <th>5xx</th>
            <th>p50</th>
            <th>p95</th>
            <th>p99</th>
          </tr>
        </thead>
        <tbody>
          {o.api.routes.map((r) => (
            <tr key={r.route}>
              <td className="mono">{r.route}</td>
              <td>{r.requests}</td>
              <td>{r.serverErrors}</td>
              <td>{ms(r.p50Ms)}</td>
              <td style={{ color: (r.p95Ms ?? 0) > 300 ? 'var(--danger)' : undefined }}>
                {ms(r.p95Ms)}
              </td>
              <td>{ms(r.p99Ms)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
