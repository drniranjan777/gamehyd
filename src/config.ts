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
  // ---- Store and payments (Phase 5) ----
  /** Accept HMAC-signed fake receipts on platform DEV. Never in production. */
  DEV_PURCHASES_ENABLED: bool.default('false'),
  DEV_PURCHASE_SECRET: z.string().min(32).optional(),
  /** Google Play: package name and service-account JSON (androidpublisher scope). Absent = Android verification off. */
  GOOGLE_PLAY_PACKAGE_NAME: z.string().optional(),
  GOOGLE_SERVICE_ACCOUNT_JSON: z.string().optional(),
  /** Pub/Sub push authentication for Real-time developer notifications. */
  GOOGLE_RTDN_AUDIENCE: z.string().optional(),
  GOOGLE_RTDN_SERVICE_ACCOUNT: z.string().optional(),
  /** Overridable for tests (a local mock of Google); production uses the defaults. */
  GOOGLE_API_BASE_URL: z.string().url().default('https://androidpublisher.googleapis.com'),
  GOOGLE_TOKEN_URL: z.string().url().default('https://oauth2.googleapis.com/token'),
  GOOGLE_RTDN_JWKS_URL: z.string().url().default('https://www.googleapis.com/oauth2/v3/certs'),
  /** Apple: bundle id and the trusted root certificate (PEM) for StoreKit 2 signed data. Absent = iOS off. */
  APPLE_BUNDLE_ID: z.string().optional(),
  APPLE_ROOT_CERT_PEM: z.string().optional(),
  /** Pending orders older than this are cancelled by reconciliation. */
  ORDER_PENDING_TTL_HOURS: z.coerce.number().int().min(1).max(720).default(24),
  RECONCILE_INTERVAL_SECONDS: z.coerce.number().int().min(0).max(86400).default(600),
  ACCOUNT_DELETION_GRACE_DAYS: z.coerce.number().int().min(1).max(90).default(30),
  // ---- Admin portal (Phase 5) ----
  /** AES-256-GCM key for admin TOTP secrets, hex. */
  ADMIN_TOTP_KEY_HEX: z.string().regex(/^[0-9a-fA-F]{64}$/, '32 bytes of hex'),
  ADMIN_SESSION_HOURS: z.coerce.number().int().min(1).max(24).default(8),
  // ---- Phase 6 ----
  /** FORMING matches the assigned server never confirmed are cancelled and their players requeued after this. */
  UNHOSTED_MATCH_TIMEOUT_SECONDS: z.coerce.number().int().min(10).max(600).default(45),
  /** Raw client telemetry is deleted after this many days. */
  TELEMETRY_RETENTION_DAYS: z.coerce.number().int().min(7).max(400).default(90),
  // ---- Phase 7 ----
  /** How often each API instance writes its latency histograms (0 = only on demand / shutdown). */
  OPS_FLUSH_SECONDS: z.coerce.number().int().min(0).max(3600).default(60),
  OPS_RETENTION_DAYS: z.coerce.number().int().min(1).max(400).default(30),
  /** Match-server tick time above which a minute counts as over budget (30 Hz leaves 33 ms; keep half spare). */
  TICK_BUDGET_MS: z.coerce.number().min(1).max(100).default(16),
  /** Cloud cost of one match server per hour, for the cost-per-match estimate (set from the chosen instance type). */
  SERVER_COST_PER_HOUR_INR: z.coerce.number().min(0).max(100_000).default(25),
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
  if (result.data.DEV_PURCHASES_ENABLED && !result.data.DEV_PURCHASE_SECRET) {
    throw new Error(
      'Invalid configuration: DEV_PURCHASE_SECRET is required when DEV_PURCHASES_ENABLED=true',
    );
  }
  return result.data;
}
