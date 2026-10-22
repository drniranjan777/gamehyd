import Link from 'next/link';
import type { ReactNode } from 'react';
import { logout } from '../../lib/actions';
import { me } from '../../lib/api';

export const dynamic = 'force-dynamic';

/** Navigation shows only what the admin's roles allow; the API enforces the same permissions. */
const NAV: { href: string; label: string; permission?: string }[] = [
  { href: '/', label: 'Overview' },
  { href: '/players', label: 'Players', permission: 'players.read' },
  { href: '/reports', label: 'Reports', permission: 'reports.read' },
  { href: '/matches', label: 'Matches', permission: 'matches.read' },
  { href: '/catalog', label: 'Catalogue', permission: 'catalog.read' },
  { href: '/orders', label: 'Orders', permission: 'orders.read' },
  { href: '/liveops', label: 'Live ops' },
  { href: '/audit', label: 'Audit log', permission: 'audit.read' },
  { href: '/admins', label: 'Admins', permission: 'admins.manage' },
];

export default async function PortalLayout({ children }: { children: ReactNode }) {
  const admin = await me();
  return (
    <div className="shell">
      <nav className="nav">
        <div className="brand">RWBR Ops</div>
        {NAV.filter((n) => !n.permission || admin.permissions.includes(n.permission)).map((n) => (
          <Link key={n.href} href={n.href}>
            {n.label}
          </Link>
        ))}
        <div className="who">
          {admin.displayName}
          <br />
          {admin.roles.join(', ')}
          <form action={logout}>
            <button type="submit" className="ghost">
              Sign out
            </button>
          </form>
        </div>
      </nav>
      <main className="content">{children}</main>
    </div>
  );
}
