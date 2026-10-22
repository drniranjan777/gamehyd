import Link from 'next/link';
import { api, qs, type Schemas } from '../../../lib/api';
import { when } from '../../../lib/format';

export default async function PlayersPage({
  searchParams,
}: {
  searchParams: Promise<{ q?: string }>;
}) {
  const { q } = await searchParams;
  const result = q?.trim()
    ? await api<Schemas['AdminPlayerList']>(`/admin/players${qs({ q: q.trim() })}`)
    : null;
  return (
    <>
      <h1>Players</h1>
      <form className="form inline" method="get">
        <label>
          Player id or display name
          <input name="q" defaultValue={q} maxLength={60} autoFocus />
        </label>
        <button type="submit">Search</button>
      </form>
      {result && (
        <>
          <h2>
            {result.items.length === 50 ? 'First 50 matches' : `${result.items.length} found`}
          </h2>
          <table>
            <thead>
              <tr>
                <th>Display name</th>
                <th>Status</th>
                <th>Level</th>
                <th>Joined</th>
                <th>Id</th>
              </tr>
            </thead>
            <tbody>
              {result.items.map((p) => (
                <tr key={p.playerId}>
                  <td>
                    <Link href={`/players/${p.playerId}`}>{p.displayName}</Link>
                  </td>
                  <td>
                    <span className={`badge ${p.status}`}>{p.status}</span>
                  </td>
                  <td>{p.level}</td>
                  <td>{when(p.createdAt)}</td>
                  <td className="mono">{p.playerId}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </>
  );
}
