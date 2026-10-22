import type { Metadata } from 'next';
import { redirect } from 'next/navigation';

export const metadata: Metadata = { title: 'Player profiles' };

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export default async function PlayersPage({
  searchParams,
}: {
  searchParams: Promise<{ id?: string }>;
}) {
  const { id } = await searchParams;
  const trimmed = id?.trim();
  if (trimmed && UUID.test(trimmed)) redirect(`/players/${trimmed.toLowerCase()}`);
  return (
    <main>
      <h1>Player profiles</h1>
      <p className="muted">
        Find your player id in the game under Profile. Profiles show your name, level, totals and
        recent matches.
      </p>
      <form method="get">
        <input name="id" placeholder="Player id" defaultValue={id} aria-label="Player id" />{' '}
        <button type="submit">View profile</button>
      </form>
      {trimmed && <p className="muted">That doesn’t look like a player id.</p>}
    </main>
  );
}
