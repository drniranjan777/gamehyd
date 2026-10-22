import Link from 'next/link';
import { api, qs, type Schemas } from '../../../lib/api';
import { short, when } from '../../../lib/format';

export default async function MatchesPage({
  searchParams,
}: {
  searchParams: Promise<{ status?: string }>;
}) {
  const { status } = await searchParams;
  const matches = await api<Schemas['AdminMatchList']>(
    `/admin/matches${qs({ status, limit: 100 })}`,
  );
  return (
    <>
      <h1>Matches</h1>
      <p>
        {['', 'FORMING', 'ACTIVE', 'COMPLETED', 'CANCELLED', 'TERMINATED'].map((s) => (
          <Link key={s} href={s ? `/matches?status=${s}` : '/matches'} style={{ marginRight: 12 }}>
            {s ? s.toLowerCase() : 'all'}
          </Link>
        ))}
      </p>
      <table>
        <thead>
          <tr>
            <th>Match</th>
            <th>Status</th>
            <th>Map</th>
            <th>Region</th>
            <th>Players</th>
            <th>Duration</th>
            <th>Created</th>
          </tr>
        </thead>
        <tbody>
          {matches.items.map((m) => (
            <tr key={m.matchId}>
              <td className="mono">
                <Link href={`/matches/${m.matchId}`}>{short(m.matchId)}</Link>
              </td>
              <td>
                <span className={`badge ${m.status}`}>{m.status}</span>
              </td>
              <td>{m.mapVersion}</td>
              <td>{m.region}</td>
              <td>{m.players}</td>
              <td>
                {m.durationSeconds == null ? '—' : `${Math.round(m.durationSeconds / 60)} min`}
              </td>
              <td>{when(m.createdAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
