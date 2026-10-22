import Link from 'next/link';
import { publicGet, when, type Schemas } from '../lib/api';

export default async function HomePage() {
  const events = await publicGet<Schemas['EventList']>('/events');
  const live = events?.items.filter((e) => e.live) ?? [];
  return (
    <main>
      <section className="hero">
        <h1>Drop into the city. Be the last one standing.</h1>
        <p>
          Real-World Battle Royale is a free-to-play mobile battle royale in a fictional city
          inspired by Hyderabad: bazaars, rooftops, lakeside promenades and old-city lanes. Coming
          to Android and iOS.
        </p>
        <p className="muted">In development. Closed beta sign-ups open later.</p>
      </section>

      {live.length > 0 && (
        <section className="card" style={{ marginTop: 24 }}>
          <span className="live">Live now</span>
          {live.map((e) => (
            <div key={e.key}>
              <h3>{e.title}</h3>
              <p className="muted">Until {when(e.endsAt)}</p>
            </div>
          ))}
        </section>
      )}

      <section className="grid">
        <div className="card">
          <h3>Fair fights</h3>
          <p>
            Every shot is decided on our servers. Purchases are cosmetic and never change how a
            weapon performs.
          </p>
        </div>
        <div className="card">
          <h3>Made for phones</h3>
          <p>
            Short matches, touch controls built for thumbs, and netcode tuned for patchy mobile
            data.
          </p>
        </div>
        <div className="card">
          <h3>A city to learn</h3>
          <p>
            Rooftop routes, market alleys and a shrinking zone that pushes every match toward the
            old city.
          </p>
        </div>
      </section>

      <p>
        <Link href="/news">See news and events →</Link>
      </p>
    </main>
  );
}
