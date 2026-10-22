import type { Metadata } from 'next';
import Link from 'next/link';
import type { ReactNode } from 'react';
import './globals.css';

export const metadata: Metadata = {
  title: { default: 'Real-World Battle Royale', template: '%s · Real-World Battle Royale' },
  description: 'A free-to-play mobile battle royale set in a fictional city inspired by Hyderabad.',
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en">
      <body>
        <header className="site">
          <div className="wrap">
            <Link href="/" className="brand">
              RWBR
            </Link>
            <nav>
              <Link href="/news">News &amp; events</Link>
              <Link href="/players">Players</Link>
              <Link href="/support">Support</Link>
            </nav>
          </div>
        </header>
        <div className="wrap">{children}</div>
        <footer className="site">
          <div className="wrap">
            <Link href="/privacy">Privacy</Link>
            <Link href="/terms">Terms</Link>
            <Link href="/account-deletion">Delete your account</Link>
            <Link href="/support">Support</Link>
            <p>
              No pay-to-win: purchases are cosmetic only. Fictional city; not affiliated with any
              real place or brand.
            </p>
          </div>
        </footer>
      </body>
    </html>
  );
}
