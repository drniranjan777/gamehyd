import Link from 'next/link';
import { ActionForm } from '../../../components/ActionForm';
import { reconcile } from '../../../lib/actions';
import { api, can, qs, type Schemas } from '../../../lib/api';
import { money, short, when } from '../../../lib/format';

export default async function OrdersPage({
  searchParams,
}: {
  searchParams: Promise<{ status?: string; playerId?: string }>;
}) {
  const { status, playerId } = await searchParams;
  const orders = await api<Schemas['AdminOrderList']>(
    `/admin/orders${qs({ status, playerId, limit: 100 })}`,
  );
  return (
    <>
      <h1>Orders</h1>
      <div className="row" style={{ alignItems: 'flex-end', marginBottom: 12 }}>
        <form className="form inline" method="get">
          <label>
            Status
            <select name="status" defaultValue={status ?? ''}>
              <option value="">any</option>
              {['PENDING', 'FULFILLED', 'REFUNDED', 'CANCELLED', 'FAILED'].map((s) => (
                <option key={s}>{s}</option>
              ))}
            </select>
          </label>
          <label>
            Player id
            <input name="playerId" defaultValue={playerId} pattern="[0-9a-fA-F-]{36}" size={38} />
          </label>
          <button type="submit">Filter</button>
        </form>
        {(await can('orders.reconcile')) && (
          <ActionForm action={reconcile} submit="Run reconciliation now" inline />
        )}
      </div>
      <table>
        <thead>
          <tr>
            <th>Order</th>
            <th>Player</th>
            <th>Product</th>
            <th>Platform</th>
            <th>Price</th>
            <th>Status</th>
            <th>Created</th>
          </tr>
        </thead>
        <tbody>
          {orders.items.map((o) => (
            <tr key={o.orderId}>
              <td className="mono">
                <Link href={`/orders/${o.orderId}`}>{short(o.orderId)}</Link>
              </td>
              <td className="mono">
                <Link href={`/players/${o.playerId}`}>{short(o.playerId)}</Link>
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
