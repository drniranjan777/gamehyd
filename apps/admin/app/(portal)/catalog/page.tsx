import Link from 'next/link';
import { api, can, qs, type Schemas } from '../../../lib/api';
import { grantText, money, when } from '../../../lib/format';

const STATUSES = ['', 'DRAFT', 'PENDING_APPROVAL', 'PUBLISHED', 'RETIRED'];

export default async function CatalogPage({
  searchParams,
}: {
  searchParams: Promise<{ status?: string }>;
}) {
  const { status } = await searchParams;
  const products = await api<Schemas['AdminProductList']>(
    `/admin/catalog/products${qs({ status })}`,
  );
  return (
    <>
      <h1>Store catalogue</h1>
      <p>
        {STATUSES.map((s) => (
          <Link
            key={s}
            href={s ? `/catalog?status=${s}` : '/catalog'}
            style={{ marginRight: 12, fontWeight: (status ?? '') === s ? 700 : 400 }}
          >
            {s ? s.replace('_', ' ').toLowerCase() : 'all'}
          </Link>
        ))}
        {(await can('catalog.write')) && (
          <Link href="/catalog/new" style={{ float: 'right' }}>
            + New product
          </Link>
        )}
      </p>
      <table>
        <thead>
          <tr>
            <th>Product</th>
            <th>Status</th>
            <th>Price</th>
            <th>Grants</th>
            <th>Author / approver</th>
            <th>Updated</th>
          </tr>
        </thead>
        <tbody>
          {products.items.map((p) => (
            <tr key={p.productKey}>
              <td>
                <Link href={`/catalog/${p.productKey}`}>{p.displayName}</Link>
                <div className="mono muted">{p.productKey}</div>
              </td>
              <td>
                <span className={`badge ${p.status}`}>{p.status}</span>
              </td>
              <td>{money(p.price)}</td>
              <td>{p.grants.map(grantText).join(', ')}</td>
              <td className="mono">
                {p.createdBy}
                <br />
                {p.approvedBy ?? '—'}
              </td>
              <td>{when(p.updatedAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="muted">
        Products publish only after someone other than the author approves them. Published prices
        never change: retire the product and create a new one.
      </p>
    </>
  );
}
