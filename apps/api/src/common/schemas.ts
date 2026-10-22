import { z } from 'zod';
import { Invalid } from './api-error';

/** Shared request fragments (mirrors components in packages/api-contracts/openapi.yaml). */
export const displayName = z
  .string()
  .min(3)
  .max(24)
  .regex(/^[A-Za-z0-9_. -]+$/, 'letters, digits, space, dot, dash and underscore only');

export const uuid = z.string().uuid();

export function parseUuid(value: string, what: string): string {
  const r = uuid.safeParse(value);
  if (!r.success)
    throw Invalid(`${what} must be a UUID`, [{ path: what, message: 'invalid uuid' }]);
  return r.data;
}

/** Compares dotted versions numerically: -1, 0 or 1. */
export function compareVersions(a: string, b: string): number {
  const pa = a.split('.').map(Number),
    pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return Math.sign(d);
  }
  return 0;
}
