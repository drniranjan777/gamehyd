import type { Metadata } from 'next';
import Link from 'next/link';
import { supportEmail } from '../../lib/api';

export const metadata: Metadata = { title: 'Support' };

const FAQ: [string, React.ReactNode][] = [
  [
    'I bought something but didn’t receive it',
    'Purchases are checked with Google Play or the App Store before items are added, which can take a minute. If it is still missing after an hour, email us with the order number from your store receipt.',
  ],
  [
    'I was suspended or banned',
    'Suspensions end automatically when their time is up. To appeal any decision, email us with your player id; a different moderator reviews appeals.',
  ],
  [
    'How do I report a cheater?',
    'Reporting from the match results screen is coming before launch. Until then, email us the player’s name and when the match ended. Moderators review every report; we cannot share the outcome for privacy reasons.',
  ],
  [
    'How do I delete my account?',
    <Link key="d" href="/account-deletion">
      See account deletion.
    </Link>,
  ],
  [
    'I lost my progress after reinstalling',
    'Guest accounts live on the device. Linking Google Play Games or Game Center, coming before launch, will keep your progress across devices.',
  ],
];

export default function SupportPage() {
  return (
    <main className="prose">
      <h1>Support</h1>
      <p>
        Email <a href={`mailto:${supportEmail()}`}>{supportEmail()}</a> with your player id (Profile
        in the game). We reply within [2 working days].
      </p>
      {FAQ.map(([q, a]) => (
        <section key={q}>
          <h2>{q}</h2>
          <p>{a}</p>
        </section>
      ))}
    </main>
  );
}
