import Link from 'next/link';
import { ActionForm } from '../../../../components/ActionForm';
import { ProductFields } from '../../../../components/ProductFields';
import { productTransition, updateProduct } from '../../../../lib/actions';
import { api, can, me, qs, type Schemas } from '../../../../lib/api';
import { grantText, money, when } from '../../../../lib/format';

export default async function ProductPage({ params }: { params: Promise<{ key: string }> }) {
  const { key } = await params;
  const [p, admin, mayWrite, mayApprove] = await Promise.all([
    api<Schemas['AdminProduct']>(`/admin/catalog/products/${encodeURIComponent(key)}`),
    me(),
    can('catalog.write'),
    can('catalog.approve'),
  ]);
  const cosmetics =
    p.status === 'DRAFT' && mayWrite
      ? await api<Schemas['CosmeticList']>('/admin/catalog/cosmetics')
      : null;
  const history = (await can('audit.read'))
    ? await api<Schemas['AuditPage']>(
        `/admin/audit${qs({ entityType: 'product', entityId: p.productKey, limit: 50 })}`,
      )
    : null;
  const isAuthor = p.createdBy === admin.actor;
  const step = (s: 'submit' | 'approve' | 'reject' | 'retire') =>
    productTransition.bind(null, p.productKey, s);

  return (
    <>
      <p>
        <Link href="/catalog">← Catalogue</Link>
      </p>
      <h1>
        {p.displayName} <span className={`badge ${p.status}`}>{p.status}</span>
      </h1>
      <div className="row">
        <div className="panel">
          <dl className="kv">
            <dt>Key</dt>
            <dd className="mono">{p.productKey}</dd>
            <dt>Price</dt>
            <dd>{money(p.price)}</dd>
            <dt>Grants</dt>
            <dd>{p.grants.map(grantText).join(', ')}</dd>
            <dt>Store ids</dt>
            <dd className="mono">
              {Object.entries(p.skus)
                .map(([k, v]) => `${k}: ${v}`)
                .join(', ') || '—'}
            </dd>
            <dt>On sale</dt>
            <dd>
              {when(p.availableFrom)} → {when(p.availableUntil)}
            </dd>
            <dt>Author</dt>
            <dd className="mono">{p.createdBy}</dd>
            <dt>Approved by</dt>
            <dd className="mono">{p.approvedBy ?? '—'}</dd>
            <dt>Published</dt>
            <dd>{when(p.publishedAt)}</dd>
          </dl>
        </div>
        <div className="panel">
          <h2 style={{ marginTop: 0 }}>Workflow</h2>
          {p.status === 'DRAFT' && mayWrite && (
            <ActionForm action={step('submit')} submit="Submit for approval" />
          )}
          {p.status === 'PENDING_APPROVAL' && mayApprove && !isAuthor && (
            <>
              <ActionForm
                action={step('approve')}
                submit="Approve and publish"
                confirm="Publish this product to players now?"
              />
              <ActionForm action={step('reject')} submit="Send back to draft" danger>
                <input
                  name="reason"
                  placeholder="What needs to change"
                  minLength={3}
                  maxLength={500}
                  required
                />
              </ActionForm>
            </>
          )}
          {p.status === 'PENDING_APPROVAL' && isAuthor && (
            <p className="muted">
              Waiting for another approver: authors cannot approve their own products.
            </p>
          )}
          {p.status === 'PUBLISHED' && mayApprove && (
            <ActionForm
              action={step('retire')}
              submit="Retire"
              danger
              confirm="Stop selling this product? This cannot be undone."
            >
              <input name="reason" placeholder="Reason" minLength={3} maxLength={500} required />
            </ActionForm>
          )}
          {p.status === 'RETIRED' && (
            <p className="muted">Retired products are kept for order history and never return.</p>
          )}
        </div>
      </div>

      {cosmetics && (
        <div className="panel">
          <h2 style={{ marginTop: 0 }}>Edit draft</h2>
          <ActionForm action={updateProduct.bind(null, p.productKey)} submit="Save draft">
            <ProductFields cosmetics={cosmetics.items} product={p} />
          </ActionForm>
        </div>
      )}

      {history && (
        <>
          <h2>History</h2>
          <table>
            <thead>
              <tr>
                <th>When</th>
                <th>Action</th>
                <th>By</th>
                <th>Reason</th>
              </tr>
            </thead>
            <tbody>
              {history.items.map((e) => (
                <tr key={e.id}>
                  <td>{when(e.createdAt)}</td>
                  <td>{e.action}</td>
                  <td className="mono">{e.actor}</td>
                  <td>{e.reason ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </>
  );
}
