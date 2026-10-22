import Link from 'next/link';
import { api, type Schemas } from '../../../../lib/api';
import { when } from '../../../../lib/format';

export default async function MatchPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const m = await api<Schemas['AdminMatchDetail']>(`/admin/matches/${id}`);
  return (
    <>
      <h1>
        Match <span className="mono">{m.matchId}</span>{' '}
        <span className={`badge ${m.status}`}>{m.status}</span>
      </h1>
      <div className="panel">
        <dl className="kv">
          <dt>Map</dt>
          <dd>{m.mapVersion}</dd>
          <dt>Server</dt>
          <dd className="mono">{m.server}</dd>
          <dt>Ended because</dt>
          <dd>{m.completionReason ?? '—'}</dd>
          <dt>Created / ended</dt>
          <dd>
            {when(m.createdAt)} / {when(m.endedAt)}
          </dd>
        </dl>
      </div>
      <h2>Placements</h2>
      <table>
        <thead>
          <tr>
            <th>#</th>
            <th>Player</th>
            <th>Kills</th>
            <th>Survived</th>
          </tr>
        </thead>
        <tbody>
          {m.participants.map((p) => (
            <tr key={p.playerId}>
              <td>{p.placement ?? '—'}</td>
              <td>
                <Link href={`/players/${p.playerId}`}>{p.displayName}</Link>
              </td>
              <td>{p.kills ?? '—'}</td>
              <td>{p.survivalSeconds == null ? '—' : `${p.survivalSeconds} s`}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <h2>Result submissions from the match server</h2>
      <table>
        <thead>
          <tr>
            <th>Received</th>
            <th>Outcome</th>
            <th>Reason</th>
          </tr>
        </thead>
        <tbody>
          {m.resultSubmissions.map((r, i) => (
            <tr key={i}>
              <td>{when(r.receivedAt)}</td>
              <td>{r.outcome}</td>
              <td>{r.reason ?? '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
