import type { Metadata } from 'next';
import { DraftNotice } from '../draft-notice';

export const metadata: Metadata = { title: 'Terms of service' };

export default function TermsPage() {
  return (
    <main className="prose">
      <h1>Terms of service</h1>
      <DraftNotice />
      <h2>The game</h2>
      <p>
        Real-World Battle Royale is free to play. [Company legal name] grants you a personal,
        non-transferable licence to play it. The city in the game is fictional; any resemblance to
        real places is inspiration only.
      </p>
      <h2>Fair play</h2>
      <p>
        Cheats, modified clients, exploiting bugs, automating play, harassment and offensive names
        are not allowed. We may warn, suspend or ban accounts that break these rules. Suspensions
        end automatically; you can appeal any sanction through support.
      </p>
      <h2>Purchases</h2>
      <p>
        Items in the store are cosmetic and never change how the game plays. Coins are earned by
        playing and cannot be bought. Gems and real-money items are sold through Google Play or the
        App Store under their terms, and their refund policies apply. If a payment is refunded or
        reversed, the items it bought are removed; currency already spent cannot be reclaimed and
        may be recorded against the account.
      </p>
      <h2>Your account</h2>
      <p>
        Guest progress is tied to your device until you link a platform account; uninstalling may
        lose it. You can delete your account at any time.
      </p>
      <h2>Changes and availability</h2>
      <p>
        We update the game, its balance and its store, and may pause features for maintenance or
        safety. [Liability, governing law (India), dispute resolution and contact details to be
        completed by counsel.]
      </p>
    </main>
  );
}
