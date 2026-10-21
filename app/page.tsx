// Phase 0 placeholder: shows platform API health. MFA sign-in, RBAC and audit arrive in Phase 3/5.
export const dynamic = 'force-dynamic';

type Health = { ok: true; schemaVersion: string | null } | { ok: false; reason: string };

async function getHealth(): Promise<Health> {
  const base = process.env.API_BASE_URL ?? 'http://localhost:3001/api/v1';
  try {
    const res = await fetch(`${base}/health/ready`, {
      cache: 'no-store',
      signal: AbortSignal.timeout(2000),
    });
    if (!res.ok) return { ok: false, reason: `API returned ${res.status}` };
    const body = (await res.json()) as { schemaVersion: string | null };
    return { ok: true, schemaVersion: body.schemaVersion };
  } catch {
    return { ok: false, reason: 'API is not reachable' };
  }
}

export default async function OverviewPage() {
  const health = await getHealth();
  return (
    <main style={{ maxWidth: 720, margin: '0 auto', padding: '48px 16px' }}>
      <h1 style={{ fontSize: 28 }}>Operations portal</h1>
      <section
        style={{ background: '#fff', borderRadius: 8, padding: 16, border: '1px solid #dde1e6' }}
      >
        <h2 style={{ fontSize: 16, marginTop: 0 }}>Platform API</h2>
        {health.ok ? (
          <p>
            Healthy — database schema version <strong>{health.schemaVersion ?? 'none'}</strong>
          </p>
        ) : (
          <p style={{ color: '#b42318' }}>Unavailable: {health.reason}</p>
        )}
      </section>
    </main>
  );
}
