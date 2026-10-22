import { Inject, Injectable } from '@nestjs/common';
import { createHash, randomBytes } from 'node:crypto';
import { jwtVerify, SignJWT } from 'jose';
import { Unauthorized } from '../common/api-error';
import { APP_CONFIG, AppConfig } from '../config';

const ISSUER = 'rwbr-api';
const AUDIENCE = 'rwbr-client';

/** 32 random bytes, base64url (43 chars): refresh tokens and guest device secrets. */
export function randomToken(): string {
  return randomBytes(32).toString('base64url');
}

export function sha256(value: string | Buffer): Buffer {
  return createHash('sha256').update(value).digest();
}

@Injectable()
export class TokenService {
  private readonly key: Uint8Array;

  constructor(@Inject(APP_CONFIG) private readonly config: AppConfig) {
    this.key = new TextEncoder().encode(config.JWT_SECRET);
  }

  get accessTtlSeconds(): number {
    return this.config.ACCESS_TOKEN_TTL_SECONDS;
  }

  async signAccess(userId: string): Promise<string> {
    return new SignJWT({ typ: 'access' })
      .setProtectedHeader({ alg: 'HS256' })
      .setSubject(userId)
      .setIssuer(ISSUER)
      .setAudience(AUDIENCE)
      .setIssuedAt()
      .setExpirationTime(`${this.config.ACCESS_TOKEN_TTL_SECONDS}s`)
      .sign(this.key);
  }

  /** Returns the user id, or throws 401 for anything invalid, expired or of the wrong type. */
  async verifyAccess(token: string): Promise<string> {
    try {
      const { payload } = await jwtVerify(token, this.key, {
        algorithms: ['HS256'],
        issuer: ISSUER,
        audience: AUDIENCE,
      });
      if (payload.typ !== 'access' || typeof payload.sub !== 'string')
        throw new Error('wrong token type');
      return payload.sub;
    } catch {
      throw Unauthorized('INVALID_TOKEN', 'Access token is invalid or expired');
    }
  }
}
