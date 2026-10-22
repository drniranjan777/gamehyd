import Link from 'next/link';
import { ActionForm } from '../../../../components/ActionForm';
import { refundOrder } from '../../../../lib/actions';
import { api, can, type Schemas } from '../../../../lib/api';
import { money, when } from '../../../../lib/format';

export default async function OrderPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const o = await api<Schemas['AdminOrderDetail']>(`/admin/orders/${id}`);
  const mayRefund = await can('orders.refund');
  return (
    <>
      <p>
        <Link href="/orders">← Orders</Link>
      </p>
      <h1>
        Order <span className="mono">{o.orderId}</span>{' '}
        <span className={`badge ${o.status}`}>{o.status}</span>
      </h1>
      <div className="row">
        <div className="panel">
          <dl className="kv">
            <dt>Player</dt>
            <dd>
              <Link className="mono" href={`/players/${o.playerId}`}>
                {o.playerId}
              </Link>
            </dd>
            <dt>Product</dt>
            <dd>{o.productKey}</dd>
            <dt>Price</dt>
            <dd>{money(o.price)}</dd>
            <dt>Platform / store id</dt>
            <dd>
              {o.platform} {o.sku && <span className="mono">{o.sku}</span>}
            </dd>
            <dt>Created / fulfilled / refunded</dt>
            <dd>
              {when(o.createdAt)} / {when(o.fulfilledAt)} / {when(o.refundedAt)}
            </dd>
            {o.failureReason && (
              <>
                <dt>Failure</dt>
                <dd>{o.failureReason}</dd>
              </>
            )}
            {o.refundShortfall > 0 && (
              <>
                <dt>Refund shortfall</dt>
                <dd>{o.refundShortfall} (already spent; could not be clawed back)</dd>
              </>
            )}
          </dl>
        </div>
        <div className="panel">
          <h2 style={{ marginTop: 0 }}>Payment</h2>
          {o.payment ? (
            <dl className="kv">
              <dt>Platform</dt>
              <dd>{o.payment.platform}</dd>
              <dt>Transaction</dt>
              <dd className="mono">{o.payment.providerTransactionId}</dd>
              <dt>Environment</dt>
              <dd>{o.payment.environment}</dd>
              <dt>Status</dt>
              <dd>{o.payment.status}</dd>
              <dt>Verified</dt>
              <dd>{when(o.payment.verifiedAt)}</dd>
            </dl>
          ) : (
            <p className="muted">
              {o.platform === 'IN_GAME' ? 'Paid from the in-game wallet.' : 'No verified payment.'}
            </p>
          )}
          {mayRefund && o.status === 'FULFILLED' && (
            <>
              <h2>Refund</h2>
              <p className="muted" style={{ fontSize: 12 }}>
                Revokes what the order granted
                {o.platform === 'IN_GAME'
                  ? ' and returns the coins.'
                  : '. Return the money in the Play Console or App Store Connect too.'}
              </p>
              <ActionForm
                action={refundOrder.bind(null, o.orderId)}
                submit="Refund in game"
                danger
                confirm="Refund this order?"
              >
                <input
                  name="reason"
                  placeholder="Reason (audited)"
                  minLength={3}
                  maxLength={500}
                  required
                />
              </ActionForm>
            </>
          )}
        </div>
      </div>
      <h2>Platform notifications</h2>
      <table>
        <thead>
          <tr>
            <th>Received</th>
            <th>Type</th>
            <th>Outcome</th>
            <th>Detail</th>
          </tr>
        </thead>
        <tbody>
          {o.events.length === 0 && (
            <tr>
              <td colSpan={4} className="muted">
                None
              </td>
            </tr>
          )}
          {o.events.map((e, i) => (
            <tr key={i}>
              <td>{when(e.receivedAt)}</td>
              <td>{e.type}</td>
              <td>
                <span className={`badge ${e.outcome}`}>{e.outcome}</span>
              </td>
              <td>{e.detail ?? '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
