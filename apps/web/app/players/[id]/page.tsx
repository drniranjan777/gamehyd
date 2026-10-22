import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { publicGet, when, type Schemas } from '../../../lib/api';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

async function load(id: string) {
  if (!UUID.test(id)) return undefined;
  return publicGet<Schemas['PublicProfile']>(`/players/${id}/public`, 30);
}

export async function generateMetadata({
  params,
}: {
  params: Promise<{ id: string }>;
}): Promise<Metadata> {
  const p = await load((await params).id);
  return { title: p ? p.displayName : 'Player not found', robots: { index: false } };
}

export default async function PlayerPage({ params }: { params: Promise<{ id: string }> }) {
  const p = await load((await params).id);
  if (!p) notFound();
  const winRate = p.matchesPlayed ? Math.round((p.wins / p.matchesPlayed) * 100) : 0;
  return (
    <main>
      <h1 style={{ marginBottom: 0 }}>{p.displayName}</h1>
      <p className="muted">
        Level {p.level} · playing since{' '}
        {new Date(p.memberSince).toLocaleDateString('en-IN', { month: 'long', year: 'numeric' })}
      </p>
      <section className="stats">
        {[
          ['Matches', p.matchesPlayed],
          ['Wins', p.wins],
          ['Win rate', `${winRate}%`],
          ['Eliminations', p.kills],
        ].map(([label, value]) => (
          <div key={label} className="card">
            <div className="value">{value}</div>
            <div className="muted">{label}</div>
          </div>
        ))}
      </section>
      <h2>Recent matches</h2>
      {p.recentMatches.length === 0 ? (
        <p className="muted">No completed matches yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>When</th>
              <th>Placement</th>
              <th>Eliminations</th>
              <th>Map</th>
            </tr>
          </thead>
          <tbody>
            {p.recentMatches.map((m) => (
              <tr key={m.matchId}>
                <td>{when(m.completedAt)}</td>
                <td>
                  #{m.placement} of {m.players}
                </td>
                <td>{m.kills}</td>
                <td className="muted">{m.mapVersion}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  );
}
