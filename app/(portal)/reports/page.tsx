import Link from 'next/link';
import { ActionForm } from '../../../components/ActionForm';
import { resolveReport } from '../../../lib/actions';
import { api, can, qs, type Schemas } from '../../../lib/api';
import { short, when } from '../../../lib/format';

const STATUSES = ['OPEN', 'UNDER_REVIEW', 'ACTIONED', 'DISMISSED'];

export default async function ReportsPage({
  searchParams,
}: {
  searchParams: Promise<{ status?: string }>;
}) {
  const { status = 'OPEN' } = await searchParams;
  const reports = await api<Schemas['AdminReportList']>(`/admin/reports${qs({ status })}`);
  const mayResolve = await can('reports.resolve');
  return (
    <>
      <h1>Player reports</h1>
      <p>
        {STATUSES.map((s) => (
          <Link
            key={s}
            href={`/reports?status=${s}`}
            style={{ marginRight: 12, fontWeight: s === status ? 700 : 400 }}
          >
            {s.replace('_', ' ').toLowerCase()}
          </Link>
        ))}
      </p>
      <table>
        <thead>
          <tr>
            <th>Filed</th>
            <th>Player</th>
            <th>Reason</th>
            <th>Details</th>
            <th>Match</th>
            {mayResolve && <th>Update</th>}
          </tr>
        </thead>
        <tbody>
          {reports.items.length === 0 && (
            <tr>
              <td colSpan={6} className="muted">
                Queue is empty.
              </td>
            </tr>
          )}
          {reports.items.map((r) => (
            <tr key={r.reportId}>
              <td>{when(r.createdAt)}</td>
              <td>
                <Link href={`/players/${r.reportedPlayerId}`}>{r.reportedDisplayName}</Link>
                <div className="mono muted">report {short(r.reportId)}</div>
              </td>
              <td>{r.reason}</td>
              <td>{r.details ?? <span className="muted">—</span>}</td>
              <td className="mono">
                {r.matchId ? <Link href={`/matches/${r.matchId}`}>{short(r.matchId)}</Link> : '—'}
              </td>
              {mayResolve && (
                <td>
                  <ActionForm action={resolveReport.bind(null, r.reportId)} submit="Save" inline>
                    <select
                      name="status"
                      defaultValue={r.status === 'OPEN' ? 'UNDER_REVIEW' : r.status}
                    >
                      {STATUSES.map((s) => (
                        <option key={s}>{s}</option>
                      ))}
                    </select>
                    <input
                      name="note"
                      placeholder="Note (audited)"
                      minLength={3}
                      maxLength={500}
                      required
                    />
                  </ActionForm>
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
      <p className="muted">
        To sanction, open the player and reference the report id; the report is marked actioned.
      </p>
    </>
  );
}
