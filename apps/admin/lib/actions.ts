'use server';

// Server actions for every portal mutation. Next.js checks the Origin header on server actions (CSRF), the session
// cookie is httpOnly + SameSite=Strict, and the API enforces permissions and writes the audit trail.
import { cookies } from 'next/headers';
import { redirect } from 'next/navigation';
import { revalidatePath } from 'next/cache';
import { api, apiBase, ApiFailure, SESSION_COOKIE, type Schemas } from './api';

export type ActionState = {
  ok?: string;
  error?: string;
  secret?: { totpSecret: string; otpauthUrl: string };
} | null;

const text = (d: FormData, name: string) => String(d.get(name) ?? '').trim();
const optional = (d: FormData, name: string) => text(d, name) || undefined;
/** `<input type="datetime-local">` values are entered in UTC in the portal. */
const utc = (d: FormData, name: string) => new Date(`${text(d, name)}:00Z`).toISOString();

/** Runs an API mutation, reporting API errors inline; refreshes `path` on success. */
async function run(path: string, ok: string, fn: () => Promise<unknown>): Promise<ActionState> {
  try {
    await fn();
  } catch (err) {
    if (err instanceof ApiFailure) return { error: `${err.message} [${err.code}]` };
    throw err; // includes Next's redirect signal
  }
  revalidatePath(path);
  return { ok };
}

// ---- Session ----

export async function login(_: ActionState, d: FormData): Promise<ActionState> {
  const res = await fetch(`${apiBase()}/admin/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      email: text(d, 'email'),
      password: String(d.get('password') ?? ''),
      code: text(d, 'code'),
    }),
    cache: 'no-store',
  }).catch(() => undefined);
  if (!res) return { error: 'The platform API is not reachable.' };
  const json = (await res.json().catch(() => ({}))) as Schemas['AdminSession'] & {
    error?: { message: string };
  };
  if (!res.ok) return { error: json.error?.message ?? `Sign-in failed (${res.status})` };
  (await cookies()).set(SESSION_COOKIE, json.token, {
    httpOnly: true,
    sameSite: 'strict',
    secure: process.env.NODE_ENV === 'production',
    path: '/',
    expires: new Date(json.expiresAt),
  });
  redirect('/');
}

export async function logout(): Promise<void> {
  try {
    await api('/admin/auth/logout', 'POST');
  } finally {
    (await cookies()).delete(SESSION_COOKIE);
  }
  redirect('/login');
}

// ---- Players and moderation ----

export async function sanction(
  playerId: string,
  _: ActionState,
  d: FormData,
): Promise<ActionState> {
  const action = text(d, 'action');
  const hours = Number(text(d, 'hours') || 0);
  return run(`/players/${playerId}`, `${action} recorded`, () =>
    api(`/admin/players/${playerId}/sanctions`, 'POST', {
      action,
      reason: text(d, 'reason'),
      expiresAt:
        action === 'SUSPEND' ? new Date(Date.now() + hours * 3600_000).toISOString() : undefined,
      reportId: optional(d, 'reportId'),
    }),
  );
}

export async function resolveReport(
  reportId: string,
  _: ActionState,
  d: FormData,
): Promise<ActionState> {
  return run('/reports', 'Report updated', () =>
    api(`/admin/reports/${reportId}`, 'PATCH', {
      status: text(d, 'status'),
      note: text(d, 'note'),
    }),
  );
}

// ---- Catalogue ----

function productBody(d: FormData) {
  const grantType = text(d, 'grantType');
  const skus: Record<string, string> = {};
  if (optional(d, 'skuAndroid')) skus.ANDROID = text(d, 'skuAndroid');
  if (optional(d, 'skuIos')) skus.IOS = text(d, 'skuIos');
  return {
    displayName: text(d, 'displayName'),
    description: text(d, 'description'),
    price: {
      currency: text(d, 'currency'),
      amountMinor: Math.round(Number(text(d, 'amountMinor'))),
    },
    grants:
      grantType === 'CURRENCY'
        ? [{ type: 'CURRENCY', currency: 'GEM', amount: Math.round(Number(text(d, 'gemAmount'))) }]
        : [{ type: 'COSMETIC', cosmeticKey: text(d, 'cosmeticKey') }],
    skus,
    availableFrom: optional(d, 'availableFrom') ? utc(d, 'availableFrom') : null,
    availableUntil: optional(d, 'availableUntil') ? utc(d, 'availableUntil') : null,
  };
}

export async function createProduct(_: ActionState, d: FormData): Promise<ActionState> {
  const key = text(d, 'key');
  const result = await run('/catalog', 'Draft created', () =>
    api('/admin/catalog/products', 'POST', { key, ...productBody(d) }),
  );
  if (result?.ok) redirect(`/catalog/${key}`);
  return result;
}

export async function updateProduct(
  key: string,
  _: ActionState,
  d: FormData,
): Promise<ActionState> {
  return run(`/catalog/${key}`, 'Draft saved', () =>
    api(`/admin/catalog/products/${key}`, 'PATCH', productBody(d)),
  );
}

export async function productTransition(
  key: string,
  step: 'submit' | 'approve' | 'reject' | 'retire',
  _: ActionState,
  d: FormData,
): Promise<ActionState> {
  const reason = optional(d, 'reason');
  return run(`/catalog/${key}`, `Product ${step === 'submit' ? 'submitted' : `${step}d`}`, () =>
    api(`/admin/catalog/products/${key}/${step}`, 'POST', reason ? { reason } : undefined),
  );
}

// ---- Orders ----

export async function refundOrder(
  orderId: string,
  _: ActionState,
  d: FormData,
): Promise<ActionState> {
  return run(`/orders/${orderId}`, 'Order refunded in game', () =>
    api(`/admin/orders/${orderId}/refund`, 'POST', { reason: text(d, 'reason') }),
  );
}

export async function reconcile(): Promise<ActionState> {
  try {
    const r = await api<Schemas['ReconcileReport']>('/admin/reconcile', 'POST');
    revalidatePath('/orders');
    return {
      ok: `Cancelled ${r.cancelledPendingOrders} stale orders; ${r.walletsWithLedgerDrift} wallets with drift; ${r.unmatchedPurchaseEvents} unmatched platform events (7 days); ${r.accountsDeleted} accounts deleted; ${r.suspensionsLifted} suspensions ended.`,
    };
  } catch (err) {
    if (err instanceof ApiFailure) return { error: err.message };
    throw err;
  }
}

// ---- Live ops ----

export async function saveConfig(
  expectedVersion: number,
  _: ActionState,
  d: FormData,
): Promise<ActionState> {
  const list = (name: string) =>
    text(d, name)
      .split(/[\s,]+/)
      .filter(Boolean);
  return run('/liveops', 'New config version published', () =>
    api('/admin/liveops/config', 'PUT', {
      expectedVersion,
      reason: text(d, 'reason'),
      config: {
        storeEnabled: d.get('storeEnabled') === 'on',
        matchmakingEnabled: d.get('matchmakingEnabled') === 'on',
        disabledProducts: list('disabledProducts'),
        disabledWeapons: list('disabledWeapons'),
        xpMultiplier: Number(text(d, 'xpMultiplier') || '1'),
        lootTable: lootTable(d),
      },
    }),
  );
}

/** The loot table fields ("loot.<name>"); blank fields are left out so the API applies its defaults. */
function lootTable(d: FormData): Record<string, number> {
  const out: Record<string, number> = {};
  for (const [key, value] of d.entries()) {
    if (!key.startsWith('loot.') || typeof value !== 'string' || value.trim() === '') continue;
    out[key.slice(5)] = Number(value);
  }
  return out;
}

export async function createEvent(_: ActionState, d: FormData): Promise<ActionState> {
  return run('/liveops', 'Event scheduled', () =>
    api('/admin/liveops/events', 'POST', {
      key: text(d, 'key'),
      title: text(d, 'title'),
      body: text(d, 'body'),
      startsAt: utc(d, 'startsAt'),
      endsAt: utc(d, 'endsAt'),
    }),
  );
}

export async function cancelEvent(key: string, _: ActionState, d: FormData): Promise<ActionState> {
  return run('/liveops', 'Event cancelled', () =>
    api(`/admin/liveops/events/${key}/cancel`, 'POST', { reason: text(d, 'reason') }),
  );
}

// ---- Admin accounts ----

export async function createAdmin(_: ActionState, d: FormData): Promise<ActionState> {
  try {
    const r = await api<Schemas['AdminCreated']>('/admin/admins', 'POST', {
      email: text(d, 'email'),
      displayName: text(d, 'displayName'),
      password: String(d.get('password') ?? ''),
      roles: d.getAll('roles').map(String),
    });
    revalidatePath('/admins');
    return {
      ok: `Created ${r.admin.email}`,
      secret: { totpSecret: r.totpSecret, otpauthUrl: r.otpauthUrl },
    };
  } catch (err) {
    if (err instanceof ApiFailure) return { error: `${err.message} [${err.code}]` };
    throw err;
  }
}

export async function setRoles(adminId: string, _: ActionState, d: FormData): Promise<ActionState> {
  return run('/admins', 'Roles updated', () =>
    api(`/admin/admins/${adminId}/roles`, 'PUT', {
      roles: d.getAll('roles').map(String),
      reason: text(d, 'reason'),
    }),
  );
}

export async function disableAdmin(
  adminId: string,
  _: ActionState,
  d: FormData,
): Promise<ActionState> {
  return run('/admins', 'Admin disabled', () =>
    api(`/admin/admins/${adminId}/disable`, 'POST', { reason: text(d, 'reason') }),
  );
}

// ---- Releases and seasons (Phase 7) ----

export async function createRelease(_: ActionState, d: FormData): Promise<ActionState> {
  return run('/releases', 'Rollout recorded', () =>
    api('/admin/releases', 'POST', {
      platform: text(d, 'platform'),
      appVersion: text(d, 'appVersion'),
      rolloutPercent: Number(text(d, 'rolloutPercent')),
      regions: text(d, 'regions')
        .split(/[\s,]+/)
        .filter(Boolean)
        .map((r) => r.toUpperCase()),
      minCrashFree: Number(text(d, 'minCrashFree') || '99.5') / 100,
      minSessions: Number(text(d, 'minSessions') || '500'),
    }),
  );
}

export async function updateRelease(id: string, _: ActionState, d: FormData): Promise<ActionState> {
  return run('/releases', 'Release updated', () =>
    api(`/admin/releases/${id}`, 'PATCH', {
      status: optional(d, 'status'),
      rolloutPercent: optional(d, 'rolloutPercent') ? Number(text(d, 'rolloutPercent')) : undefined,
      reason: text(d, 'reason'),
    }),
  );
}

/** Reward lines: "<tier> FREE|PREMIUM COSMETIC <key>" or "<tier> FREE|PREMIUM COIN|GEM <amount>". */
function parseRewards(src: string) {
  return src
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean)
    .map((line) => {
      const [tier, track, kind, value] = line.split(/\s+/);
      if (!tier || !track || !kind || !value) throw new Error(`Cannot read reward line "${line}"`);
      const reward =
        kind.toUpperCase() === 'COSMETIC'
          ? { type: 'COSMETIC', cosmeticKey: value }
          : { type: 'CURRENCY', currency: kind.toUpperCase(), amount: Number(value) };
      return { tier: Number(tier), track: track.toUpperCase(), reward };
    });
}

export async function saveSeason(_: ActionState, d: FormData): Promise<ActionState> {
  let rewards;
  try {
    rewards = parseRewards(text(d, 'rewards'));
  } catch (err) {
    return { error: (err as Error).message };
  }
  return run('/seasons', 'Draft season saved', () =>
    api('/admin/seasons', 'POST', {
      key: text(d, 'key'),
      title: text(d, 'title'),
      xpPerTier: Number(text(d, 'xpPerTier')),
      startsAt: utc(d, 'startsAt'),
      endsAt: utc(d, 'endsAt'),
      rewards,
    }),
  );
}

export async function publishSeason(key: string): Promise<ActionState> {
  return run('/seasons', 'Season published', () => api(`/admin/seasons/${key}/publish`, 'POST'));
}
