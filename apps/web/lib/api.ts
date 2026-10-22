import type { Schemas } from '@rwbr/api-contracts';

export type { Schemas };

const base = () => process.env.API_BASE_URL ?? 'http://localhost:3001/api/v1';

/** Public, unauthenticated reads, cached for a minute. Returns undefined on 404 or when the API is unreachable. */
export async function publicGet<T>(path: string, revalidate = 60): Promise<T | undefined> {
  try {
    const res = await fetch(`${base()}${path}`, {
      next: { revalidate },
      signal: AbortSignal.timeout(3000),
    });
    if (!res.ok) return undefined;
    return (await res.json()) as T;
  } catch {
    return undefined;
  }
}

export const supportEmail = () => process.env.SUPPORT_EMAIL ?? 'support@rwbr.example';

export function when(iso: string): string {
  return new Date(iso).toLocaleString('en-IN', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'Asia/Kolkata',
  });
}
