import { z } from 'zod';

const bool = z.enum(['true', 'false', '1', '0']).transform((v) => v === 'true' || v === '1');

const schema = z.object({
  PORT: z.coerce.number().int().min(1).max(65535).default(3001),
  DATABASE_URL: z.string().min(1),
  /** HS256 key for access tokens. */
  JWT_SECRET: z.string().min(32),
  ACCESS_TOKEN_TTL_SECONDS: z.coerce.number().int().min(60).max(3600).default(900),
  REFRESH_TOKEN_TTL_DAYS: z.coerce.number().int().min(1).max(90).default(30),
  /** HMAC key match servers sign /internal requests with. */
  SERVER_SHARED_SECRET: z.string().min(32),
  /** Join-ticket secret shared with match servers (ADR 0007), hex. */
  TICKET_SECRET_HEX: z.string().regex(/^[0-9a-fA-F]{64,}$/, 'at least 32 bytes of hex'),
  SEAT_TTL_SECONDS: z.coerce.number().int().min(10).max(600).default(120),
  MIN_CLIENT_VERSION: z
    .string()
    .regex(/^\d+\.\d+\.\d+$/)
    .default('0.1.0'),
  MATCH_CAPACITY: z.coerce.number().int().min(2).max(100).default(30),
  MATCH_MIN_PLAYERS: z.coerce.number().int().min(2).max(100).default(2),
  MATCH_FILL_WAIT_SECONDS: z.coerce.number().int().min(0).max(300).default(20),
  DEFAULT_MAP_VERSION: z.string().default('hyd-city-v1'),
  SERVER_HEARTBEAT_STALE_SECONDS: z.coerce.number().int().min(3).max(120).default(10),
  RATE_LIMIT_ENABLED: bool.default('true'),
  LOG_REQUESTS: bool.default('true'),
  TRUST_PROXY: bool.default('false'),
});

export type AppConfig = z.infer<typeof schema>;
export const APP_CONFIG = Symbol('APP_CONFIG');

/** Validates the environment. Errors name the variables, never their values. */
export function loadConfig(env: NodeJS.ProcessEnv = process.env): AppConfig {
  const result = schema.safeParse(env);
  if (!result.success) {
    const problems = result.error.issues.map((i) => `${i.path.join('.')}: ${i.message}`).join('; ');
    throw new Error(`Invalid configuration: ${problems}`);
  }
  if (result.data.MATCH_MIN_PLAYERS > result.data.MATCH_CAPACITY) {
    throw new Error('Invalid configuration: MATCH_MIN_PLAYERS must not exceed MATCH_CAPACITY');
  }
  return result.data;
}
