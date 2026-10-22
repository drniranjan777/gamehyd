import type { Metadata } from 'next';
import { supportEmail } from '../../lib/api';

export const metadata: Metadata = { title: 'Delete your account' };

/** Google Play requires a web page explaining account deletion that works without installing the app. */
export default function AccountDeletionPage() {
  return (
    <main className="prose">
      <h1>Delete your Real-World Battle Royale account</h1>
      <h2>In the game</h2>
      <ol>
        <li>Open the game and go to Settings.</li>
        <li>Choose Account &amp; data → Delete account, then confirm.</li>
      </ol>
      <p>
        Your account is scheduled for deletion in <strong>30 days</strong>. Until then you can sign
        in and cancel from the same screen. After 30 days it is erased and cannot be recovered.
      </p>
      <h2>Without the game</h2>
      <p>
        Email{' '}
        <a href={`mailto:${supportEmail()}?subject=Delete%20my%20account`}>{supportEmail()}</a> from
        the address you use for support, with your player id (shown on your profile). We will
        confirm ownership before scheduling the deletion.
      </p>
      <h2>What is deleted, and what is kept</h2>
      <ul>
        <li>
          <strong>Deleted:</strong> your sign-in credentials and sessions, display name, inventory
          and equipped items, and your place in matchmaking.
        </li>
        <li>
          <strong>Kept without your name:</strong> purchase and payment records, as tax and
          accounting law requires [currently planned: 8 years]; match results other players&rsquo;
          histories depend on; and moderation records needed to prevent abuse.
        </li>
      </ul>
      <p className="muted">Purchases are not refunded when you delete your account.</p>
    </main>
  );
}
