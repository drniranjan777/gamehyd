import {
  CanActivate,
  createParamDecorator,
  ExecutionContext,
  Inject,
  Injectable,
  SetMetadata,
} from '@nestjs/common';
import { Reflector } from '@nestjs/core';
import { createHash, createHmac, timingSafeEqual } from 'node:crypto';
import type { Request } from 'express';
import { TokenService } from '../auth/token.service';
import { APP_CONFIG, AppConfig } from '../config';
import { ApiError, Unauthorized } from './api-error';

export interface AuthedRequest extends Request {
  requestId?: string;
  userId?: string;
  serverAddress?: string;
  rawBody?: Buffer;
}

/** Requires a valid access token; sets req.userId. */
@Injectable()
export class AuthGuard implements CanActivate {
  constructor(private readonly tokens: TokenService) {}

  async canActivate(ctx: ExecutionContext): Promise<boolean> {
    const req = ctx.switchToHttp().getRequest<AuthedRequest>();
    const header = req.header('authorization') ?? '';
    const match = /^Bearer ([A-Za-z0-9._-]+)$/.exec(header);
    if (!match) throw Unauthorized();
    req.userId = await this.tokens.verifyAccess(match[1]!);
    return true;
  }
}

export const CurrentUser = createParamDecorator((_: unknown, ctx: ExecutionContext): string => {
  const id = ctx.switchToHttp().getRequest<AuthedRequest>().userId;
  if (!id) throw Unauthorized();
  return id;
});

// ---- Rate limiting ----

export interface RateLimitOptions {
  limit: number;
  windowSeconds: number;
  key: 'ip' | 'user';
}

const RATE_LIMIT = 'rwbr:rate-limit';
export const RateLimit = (options: RateLimitOptions) => SetMetadata(RATE_LIMIT, options);

/**
 * Fixed-window limiter, in memory per API instance. Enough for one instance; move the counters to Redis when
 * the API scales out (BRD §7.1 "rate-limit counters").
 */
@Injectable()
export class RateLimitGuard implements CanActivate {
  private readonly windows = new Map<string, { start: number; count: number }>();
  private lastSweep = Date.now();

  constructor(
    private readonly reflector: Reflector,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {}

  canActivate(ctx: ExecutionContext): boolean {
    const options = this.reflector.get<RateLimitOptions | undefined>(RATE_LIMIT, ctx.getHandler());
    if (!options || !this.config.RATE_LIMIT_ENABLED) return true;
    const req = ctx.switchToHttp().getRequest<AuthedRequest>();
    const who = options.key === 'user' ? (req.userId ?? req.ip) : req.ip;
    const key = `${ctx.getClass().name}.${ctx.getHandler().name}:${who}`;

    const now = Date.now();
    this.sweep(now);
    const w = this.windows.get(key);
    if (!w || now - w.start >= options.windowSeconds * 1000) {
      this.windows.set(key, { start: now, count: 1 });
      return true;
    }
    w.count++;
    if (w.count > options.limit) {
      throw new ApiError(429, 'RATE_LIMITED', 'Too many requests, slow down');
    }
    return true;
  }

  private sweep(now: number): void {
    if (now - this.lastSweep < 60_000) return;
    this.lastSweep = now;
    for (const [k, w] of this.windows) if (now - w.start > 3_600_000) this.windows.delete(k);
  }
}

// ---- Match server signatures ----

export const SIGNATURE_WINDOW_SECONDS = 300;

/** The string match servers sign; see the serverSignature scheme in openapi.yaml. */
export function signingPayload(
  timestamp: string,
  method: string,
  path: string,
  body: Buffer,
): string {
  return `${timestamp}.${method.toUpperCase()}.${path}.${createHash('sha256').update(body).digest('hex')}`;
}

/** Verifies X-RWBR-Signature on /internal requests; sets req.serverAddress. */
@Injectable()
export class ServerSignatureGuard implements CanActivate {
  constructor(@Inject(APP_CONFIG) private readonly config: AppConfig) {}

  canActivate(ctx: ExecutionContext): boolean {
    const req = ctx.switchToHttp().getRequest<AuthedRequest>();
    const server = req.header('x-rwbr-server');
    const timestamp = req.header('x-rwbr-timestamp') ?? '';
    const signature = req.header('x-rwbr-signature') ?? '';
    if (!server || !/^\d{9,11}$/.test(timestamp) || !/^[0-9a-f]{64}$/.test(signature)) {
      throw Unauthorized('BAD_SIGNATURE', 'Missing or malformed server signature');
    }
    if (Math.abs(Date.now() / 1000 - Number(timestamp)) > SIGNATURE_WINDOW_SECONDS) {
      throw Unauthorized('BAD_SIGNATURE', 'Signature timestamp outside the allowed window');
    }
    const path = req.originalUrl.split('?')[0]!;
    const expected = createHmac('sha256', this.config.SERVER_SHARED_SECRET)
      .update(signingPayload(timestamp, req.method, path, req.rawBody ?? Buffer.alloc(0)))
      .digest();
    const given = Buffer.from(signature, 'hex');
    if (given.length !== expected.length || !timingSafeEqual(given, expected)) {
      throw Unauthorized('BAD_SIGNATURE', 'Server signature does not match');
    }
    req.serverAddress = server;
    return true;
  }
}
