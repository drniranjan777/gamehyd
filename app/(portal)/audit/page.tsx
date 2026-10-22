import Link from 'next/link';
import { api, qs, type Schemas } from '../../../lib/api';
import { when } from '../../../lib/format';

type Filters = {
  entityType?: string;
  entityId?: string;
  actor?: string;
  action?: string;
  before?: string;
};

export default async function AuditPage({ searchParams }: { searchParams: Promise<Filters> }) {
  const f = await searchParams;
  const page = await api<Schemas['AuditPage']>(`/admin/audit${qs({ ...f, limit: 100 })}`);
  return (
    <>
      <h1>Audit log</h1>
      <form className="form inline" method="get" style={{ marginBottom: 12 }}>
        <label>
          Entity type
          <input
            name="entityType"
            defaultValue={f.entityType}
            placeholder="user, order, product…"
          />
        </label>
        <label>
          Entity id
          <input name="entityId" defaultValue={f.entityId} />
        </label>
        <label>
          Actor
          <input name="actor" defaultValue={f.actor} placeholder="admin:<id>, system" />
        </label>
        <label>
          Action
          <input name="action" defaultValue={f.action} placeholder="order.refund" />
        </label>
        <button type="submit">Filter</button>
      </form>
      <table>
        <thead>
          <tr>
            <th>When</th>
            <th>Actor</th>
            <th>Action</th>
            <th>Entity</th>
            <th>Reason</th>
            <th>Details</th>
          </tr>
        </thead>
        <tbody>
          {page.items.map((e) => (
            <tr key={e.id}>
              <td>{when(e.createdAt)}</td>
              <td className="mono">{e.actor}</td>
              <td>{e.action}</td>
              <td className="mono">
                {e.entityType}:{e.entityId}
              </td>
              <td>{e.reason ?? '—'}</td>
              <td className="mono" style={{ maxWidth: 320, wordBreak: 'break-all' }}>
                {e.details ? JSON.stringify(e.details) : '—'}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {page.nextBefore && (
        <p>
          <Link href={`/audit${qs({ ...f, before: page.nextBefore })}`}>Older entries →</Link>
        </p>
      )}
      <p className="muted">
        The audit log is append-only in the database; entries cannot be edited or deleted.
      </p>
    </>
  );
}
