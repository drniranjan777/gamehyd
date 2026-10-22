import Link from 'next/link';
import { ActionForm } from '../../../../components/ActionForm';
import { sanction } from '../../../../lib/actions';
import { api, can, type Schemas } from '../../../../lib/api';
import { money, short, when } from '../../../../lib/format';

export default async function PlayerPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const p = await api<Schemas['AdminPlayerDetail']>(`/admin/players/${id}`);
  const mayEnforce = await can('players.sanction');
  return (
    <>
      <h1>
        {p.displayName} <span className={`badge ${p.status}`}>{p.status}</span>
      </h1>
      <div className="row">
        <div className="panel">
          <dl className="kv">
            <dt>Player id</dt>
            <dd className="mono">{p.playerId}</dd>
            <dt>Level / XP</dt>
            <dd>
              {p.level} / {p.xp}
            </dd>
            <dt>Matches / wins / kills</dt>
            <dd>
              {p.matchesPlayed} / {p.wins} / {p.kills}
            </dd>
            <dt>Joined</dt>
            <dd>{when(p.createdAt)}</dd>
            <dt>Sign-in</dt>
            <dd>
              {p.signIn.map((s) => `${s.provider} (last ${when(s.lastUsedAt)})`).join(', ') ||
                'none'}
            </dd>
            <dt>Wallets</dt>
            <dd>
              {Object.entries(p.wallets)
                .map(([c, v]) => `${v} ${c}`)
                .join(', ') || 'none'}
            </dd>
            <dt>Inventory</dt>
            <dd>{p.inventory.map((i) => i.cosmeticKey).join(', ')}</dd>
          </dl>
        </div>
        {mayEnforce && p.status !== 'DELETED' && (
          <div className="panel">
            <h2 style={{ marginTop: 0 }}>Moderation</h2>
            <ActionForm
              action={sanction.bind(null, p.playerId)}
              submit="Apply"
              confirm="Apply this sanction? Suspensions and bans sign the player out immediately."
            >
              <label>
                Action
                <select name="action" defaultValue="WARN">
                  <option value="WARN">Warn</option>
                  <option value="SUSPEND">Suspend</option>
                  <option value="BAN">Ban</option>
                  <option value="UNBAN">Lift suspension or ban</option>
                </select>
              </label>
              <label>
                Suspension length in hours (suspend only)
                <input name="hours" type="number" min={1} max={8760} defaultValue={24} />
              </label>
              <label>
                Reason (recorded and audited)
                <textarea name="reason" minLength={3} maxLength={500} required />
              </label>
              <label>
                Report id (optional; marks it actioned)
                <input name="reportId" pattern="[0-9a-fA-F-]{36}" />
              </label>
            </ActionForm>
          </div>
        )}
      </div>

      <h2>Sanctions</h2>
      <table>
        <thead>
          <tr>
            <th>When</th>
            <th>Action</th>
            <th>Until</th>
            <th>Reason</th>
            <th>By</th>
          </tr>
        </thead>
        <tbody>
          {p.sanctions.length === 0 && (
            <tr>
              <td colSpan={5} className="muted">
                None
              </td>
            </tr>
          )}
          {p.sanctions.map((s) => (
            <tr key={s.id}>
              <td>{when(s.createdAt)}</td>
              <td>{s.action}</td>
              <td>{when(s.expiresAt)}</td>
              <td>{s.reason}</td>
              <td>{s.by}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <h2>Reports against this player</h2>
      <table>
        <thead>
          <tr>
            <th>When</th>
            <th>Reason</th>
            <th>Status</th>
            <th>Report id</th>
          </tr>
        </thead>
        <tbody>
          {p.reports.map((r) => (
            <tr key={r.reportId}>
              <td>{when(r.createdAt)}</td>
              <td>{r.reason}</td>
              <td>
                <span className={`badge ${r.status}`}>{r.status}</span>
              </td>
              <td className="mono">{r.reportId}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <h2>Recent orders</h2>
      <table>
        <thead>
          <tr>
            <th>Order</th>
            <th>Product</th>
            <th>Platform</th>
            <th>Price</th>
            <th>Status</th>
            <th>Created</th>
          </tr>
        </thead>
        <tbody>
          {p.orders.map((o) => (
            <tr key={o.orderId}>
              <td className="mono">
                <Link href={`/orders/${o.orderId}`}>{short(o.orderId)}</Link>
              </td>
              <td>{o.productKey}</td>
              <td>{o.platform}</td>
              <td>{money(o.price)}</td>
              <td>
                <span className={`badge ${o.status}`}>{o.status}</span>
              </td>
              <td>{when(o.createdAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
