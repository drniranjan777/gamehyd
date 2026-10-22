import type { Metadata } from 'next';
import { publicGet, when, type Schemas } from '../../lib/api';

export const metadata: Metadata = { title: 'News & events' };

export default async function NewsPage() {
  const events = await publicGet<Schemas['EventList']>('/events');
  return (
    <main>
      <h1>News &amp; events</h1>
      {!events && (
        <p className="muted">Events are unavailable right now. Please try again later.</p>
      )}
      {events?.items.length === 0 && (
        <p className="muted">Nothing scheduled yet. Check back soon.</p>
      )}
      {events?.items.map((e) => (
        <article key={e.key} className="card" style={{ marginBottom: 16 }}>
          {e.live && <span className="live">Live now</span>}
          <h2 style={{ margin: '4px 0' }}>{e.title}</h2>
          <p className="muted">
            {when(e.startsAt)} – {when(e.endsAt)} (IST)
          </p>
          {e.body && <p style={{ whiteSpace: 'pre-line' }}>{e.body}</p>}
        </article>
      ))}
    </main>
  );
}
