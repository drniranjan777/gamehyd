import type { Metadata } from 'next';
import type { ReactNode } from 'react';

export const metadata: Metadata = {
  title: 'Real-World Battle Royale',
  description: 'A mobile battle royale set in a fictional Hyderabad-inspired city.',
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en">
      <body
        style={{
          margin: 0,
          fontFamily: 'system-ui, sans-serif',
          background: '#0f1115',
          color: '#f2f2f2',
        }}
      >
        {children}
      </body>
    </html>
  );
}
