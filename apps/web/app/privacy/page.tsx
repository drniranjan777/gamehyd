import type { Metadata } from 'next';
import Link from 'next/link';
import { supportEmail } from '../../lib/api';
import { DraftNotice } from '../draft-notice';

export const metadata: Metadata = { title: 'Privacy policy' };

export default function PrivacyPage() {
  return (
    <main className="prose">
      <h1>Privacy policy</h1>
      <DraftNotice />
      <p>
        This policy explains what Real-World Battle Royale (&ldquo;the game&rdquo;), operated by
        [company legal name], collects, why, and the choices you have. It is written for
        India&rsquo;s Digital Personal Data Protection Act, 2023 and the Google Play and App Store
        requirements.
      </p>

      <h2>What we collect</h2>
      <ul>
        <li>
          <strong>Account:</strong> a random player id, a device credential created by the game (we
          store only a hash of its secret), and the display name you choose. Signing in with Google
          Play Games or Game Center, when available, adds that service&rsquo;s player id.
        </li>
        <li>
          <strong>Gameplay:</strong> matches you play, placements, eliminations, survival time,
          experience and level, and the cosmetics you own and equip.
        </li>
        <li>
          <strong>Purchases:</strong> orders, prices, the store&rsquo;s transaction id and the
          outcome of verifying it. We never receive your card or UPI details; payment is handled by
          Google Play or the App Store. Purchase tokens are stored only as one-way hashes.
        </li>
        <li>
          <strong>Safety:</strong> reports you file or that are filed about you, and any moderation
          decisions.
        </li>
        <li>
          <strong>Technical:</strong> app version, request logs with IP address and device
          user-agent, kept for security and troubleshooting for [30] days.
        </li>
      </ul>

      <h2>Why we use it</h2>
      <p>
        To run matches and keep them fair (the server decides every hit), to save your progress and
        purchases, to stop cheating, fraud and abuse, to answer support requests, and to understand
        how the game performs. We do not sell personal data and the game shows no third-party
        advertising.
      </p>

      <h2>Who we share it with</h2>
      <p>
        Hosting and infrastructure providers that run the game for us, Google and Apple for
        purchases and sign-in, and authorities where the law requires it. [List of processors to be
        confirmed.]
      </p>

      <h2>Public information</h2>
      <p>
        Your display name, level, totals and recent match results are shown on your public{' '}
        <Link href="/players">player profile</Link> and to other players in matches.
      </p>

      <h2>How long we keep it</h2>
      <p>
        Account and gameplay data are kept while your account exists. When you{' '}
        <Link href="/account-deletion">delete your account</Link>, sign-in data, inventory and your
        display name are erased after a 30-day grace period. Purchase and payment records are kept,
        separated from your profile, for as long as tax and accounting law requires [currently
        planned: 8 years].
      </p>

      <h2>Your rights</h2>
      <p>
        You can access, correct (change your display name in game) and erase your data, and withdraw
        consent by deleting your account. Contact our grievance officer, [name], at{' '}
        <a href={`mailto:${supportEmail()}`}>{supportEmail()}</a>. We reply within [30] days.
      </p>

      <h2>Children</h2>
      <p>
        The game is intended for players aged [13/16/18] and over, subject to the age rating
        decision.
      </p>

      <p className="muted">Last updated: [date of approval].</p>
    </main>
  );
}
