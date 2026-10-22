import Link from 'next/link';
import { api, qs, type Schemas } from '../../../lib/api';
import { when } from '../../../lib/format';

const pct = (v: number | null | undefined, digits = 1) =>
  v === null || v === undefined ? '—' : `${(v * 100).toFixed(digits)}%`;
const num = (v: number | null | undefined, unit = '') =>
  v === null || v === undefined ? '—' : `${Math.round(v * 10) / 10}${unit}`;

const GATE_LABEL: Record<string, string> = {
  crash_free_sessions: 'Crash-free sessions',
  match_completion: 'Match completion',
  purchase_duplication: 'Purchase integrity',
  security_issues: 'Security findings',
  legal_review: 'Legal review',
  on_call: 'On-call readiness',
};

export default async function MetricsPage({
  searchParams,
}: {
  searchParams: Promise<{ days?: string }>;
}) {
  const { days = '7' } = await searchParams;
  const m = await api<Schemas['Metrics']>(`/admin/metrics${qs({ days })}`);
  const tiles: [string, string, boolean?][] = [
    [
      'Crash-free sessions',
      pct(m.sessions.crashFreeRate, 2),
      (m.sessions.crashFreeRate ?? 1) < 0.995,
    ],
    ['Sessions', String(m.sessions.total)],
    ['Match completion', pct(m.matches.completionRate), (m.matches.completionRate ?? 1) < 0.95],
    ['Disconnects / 100 joins', num(m.disconnects.per100Participations)],
    ['Queue time p50 / p95', `${num(m.queueSeconds.p50, ' s')} / ${num(m.queueSeconds.p95, ' s')}`],
    ['D1 retention', `${pct(m.retention.d1.rate)} of ${m.retention.d1.cohort}`],
    ['D7 retention', `${pct(m.retention.d7.rate)} of ${m.retention.d7.cohort}`],
    [
      'Payer conversion',
      `${pct(m.economy.payerConversion, 2)} (${m.economy.payers} of ${m.economy.activePlayers})`,
    ],
  ];
  return (
    <>
      <h1>Beta metrics</h1>
      <p>
        {[1, 7, 30].map((d) => (
          <Link
            key={d}
            href={`/metrics?days=${d}`}
            style={{ marginRight: 12, fontWeight: String(d) === days ? 700 : 400 }}
          >
            last {d} day{d > 1 ? 's' : ''}
          </Link>
        ))}
        <span className="muted"> · generated {when(m.generatedAt)}</span>
      </p>

      <h2>Release gates (BRD §14)</h2>
      <table>
        <thead>
          <tr>
            <th>Gate</th>
            <th>Target</th>
            <th>Value</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {m.releaseGates.map((g) => (
            <tr key={g.key}>
              <td>{GATE_LABEL[g.key] ?? g.key}</td>
              <td>{g.target}</td>
              <td>
                {g.key === 'purchase_duplication'
                  ? num(g.value)
                  : g.value === null
                    ? '—'
                    : pct(g.value, 2)}
              </td>
              <td>
                <span
                  className={`badge ${g.status === 'PASS' ? 'ACTIVE' : g.status === 'FAIL' ? 'BANNED' : 'PENDING'}`}
                >
                  {g.status}
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <h2>Key numbers</h2>
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

      <h2>Frame rate by device (top 20 by samples)</h2>
      <table>
        <thead>
          <tr>
            <th>Device</th>
            <th>Platform</th>
            <th>Samples</th>
            <th>Average FPS</th>
            <th>5th-percentile FPS</th>
          </tr>
        </thead>
        <tbody>
          {m.fpsByDevice.length === 0 && (
            <tr>
              <td colSpan={5} className="muted">
                No samples yet.
              </td>
            </tr>
          )}
          {m.fpsByDevice.map((d) => (
            <tr key={`${d.platform}-${d.deviceModel}`}>
              <td>{d.deviceModel}</td>
              <td>{d.platform}</td>
              <td>{d.samples}</td>
              <td>{d.avgFps}</td>
              <td style={{ color: d.p5Fps < 25 ? 'var(--danger)' : undefined }}>{d.p5Fps}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <p className="muted">
        Purchase integrity counts duplicate platform transactions, paid orders without a verified
        payment and wallets whose balance differs from the ledger (
        {m.purchaseIntegrity.duplicateTransactions} / {m.purchaseIntegrity.paidOrdersWithoutPayment}{' '}
        / {m.purchaseIntegrity.walletsWithLedgerDrift}). Gates marked MANUAL are signed off outside
        the system; see docs/runbooks.
      </p>
    </>
  );
}
