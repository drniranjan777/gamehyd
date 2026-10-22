// Server-side only: the admin session token never reaches browser JavaScript (httpOnly cookie, server calls).
import type { Schemas } from '@rwbr/api-contracts';
import { cookies, headers } from 'next/headers';
import { redirect } from 'next/navigation';
import { cache } from 'react';

export type { Schemas };
export const SESSION_COOKIE = 'rwbr_admin_session';

export const apiBase = () => process.env.API_BASE_URL ?? 'http://localhost:3001/api/v1';

/** An API error to show the operator (message and stable code from the error envelope). */
export class ApiFailure extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
  ) {
    super(message);
  }
}

type Method = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

/** Calls the admin API with the session cookie. A missing or expired session goes back to the login page. */
export async function api<T>(path: string, method: Method = 'GET', body?: unknown): Promise<T> {
  const token = (await cookies()).get(SESSION_COOKIE)?.value;
  if (!token) redirect('/login');
  const res = await fetch(`${apiBase()}${path}`, {
    method,
    cache: 'no-store',
    headers: {
      authorization: `Admin ${token}`,
      ...(body === undefined ? {} : { 'content-type': 'application/json' }),
      'user-agent': (await headers()).get('user-agent') ?? 'rwbr-admin',
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(10_000),
  });
  if (res.status === 401) redirect('/login?expired=1');
  if (res.status === 204) return undefined as T;
  const json = (await res.json().catch(() => ({}))) as {
    error?: { code: string; message: string; details?: { path: string; message: string }[] };
  };
  if (!res.ok) {
    const detail = json.error?.details?.map((d) => `${d.path}: ${d.message}`).join('; ');
    throw new ApiFailure(
      res.status,
      json.error?.code ?? 'ERROR',
      `${json.error?.message ?? `API returned ${res.status}`}${detail ? ` (${detail})` : ''}`,
    );
  }
  return json as T;
}

/** The signed-in admin, fetched once per request. */
export const me = cache(() => api<Schemas['AdminPrincipal']>('/admin/me'));

export async function can(permission: string): Promise<boolean> {
  return (await me()).permissions.includes(permission);
}

/** Builds a query string from defined, non-empty values. */
export function qs(params: Record<string, string | number | undefined | null>): string {
  const entries = Object.entries(params).filter(
    ([, v]) => v !== undefined && v !== null && v !== '',
  );
  return entries.length ? `?${new URLSearchParams(entries.map(([k, v]) => [k, String(v)]))}` : '';
}
