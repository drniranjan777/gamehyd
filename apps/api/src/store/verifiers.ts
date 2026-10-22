import { createHash, createHmac, timingSafeEqual, X509Certificate } from 'node:crypto';
import { compactVerify, importPKCS8, importX509, SignJWT } from 'jose';
import { ApiError } from '../common/api-error';

export type StorePlatform = 'ANDROID' | 'IOS' | 'DEV';

/** A purchase the platform confirmed. Tokens and receipts are only ever kept as hashes. */
export interface VerifiedPurchase {
  providerTransactionId: string;
  sku: string;
  environment: 'PRODUCTION' | 'SANDBOX' | 'DEV';
  tokenHash: Buffer;
  /** Called after the grant is committed (Google requires acknowledgement within 3 days). */
  acknowledge?: () => Promise<void>;
}

export interface PurchaseVerifier {
  readonly platform: StorePlatform;
  /** orderId is known for a normal purchase and absent for a restore. */
  verify(proof: string, expectedSku: string, orderId?: string): Promise<VerifiedPurchase>;
}

export const Rejected = (message: string) => new ApiError(422, 'RECEIPT_REJECTED', message);
const sha256 = (s: string) => createHash('sha256').update(s).digest();

// ---------------------------------------------------------------------------------------------------------------
// DEV: HMAC-signed fake receipts for local development, automated tests and the smoke test. Off unless configured.
// ---------------------------------------------------------------------------------------------------------------

export function devReceipt(
  secret: string,
  orderId: string,
  sku: string,
  transactionId: string,
): string {
  const body = Buffer.from(JSON.stringify({ orderId, sku, transactionId })).toString('base64url');
  const sig = createHmac('sha256', secret).update(body).digest('base64url');
  return `${body}.${sig}`;
}

export class DevVerifier implements PurchaseVerifier {
  readonly platform = 'DEV' as const;

  constructor(private readonly secret: string) {}

  async verify(proof: string, expectedSku: string, orderId?: string): Promise<VerifiedPurchase> {
    const [body, sig] = proof.split('.');
    if (!body || !sig) throw Rejected('Malformed dev receipt');
    const expected = createHmac('sha256', this.secret).update(body).digest();
    const given = Buffer.from(sig, 'base64url');
    if (given.length !== expected.length || !timingSafeEqual(given, expected))
      throw Rejected('Dev receipt signature is invalid');
    let r: { orderId?: string; sku?: string; transactionId?: string };
    try {
      r = JSON.parse(Buffer.from(body, 'base64url').toString('utf8'));
    } catch {
      throw Rejected('Malformed dev receipt');
    }
    if (typeof r.transactionId !== 'string' || !r.transactionId)
      throw Rejected('Dev receipt has no transaction id');
    if (orderId && r.orderId !== orderId) throw Rejected('Receipt belongs to another order');
    if (r.sku !== expectedSku) throw Rejected('Receipt is for a different product');
    return {
      providerTransactionId: r.transactionId,
      sku: expectedSku,
      environment: 'DEV',
      tokenHash: sha256(proof),
    };
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Google Play: Android Publisher API purchases.products.get with a service account (server to server).
// ---------------------------------------------------------------------------------------------------------------

type Fetch = typeof fetch;

export class GoogleVerifier implements PurchaseVerifier {
  readonly platform = 'ANDROID' as const;
  private token?: { value: string; expires: number };

  constructor(
    private readonly packageName: string,
    private readonly serviceAccount: { client_email: string; private_key: string },
    private readonly urls = {
      api: 'https://androidpublisher.googleapis.com',
      token: 'https://oauth2.googleapis.com/token',
    },
    private readonly http: Fetch = fetch,
  ) {}

  private async accessToken(): Promise<string> {
    if (this.token && this.token.expires > Date.now() + 60_000) return this.token.value;
    const key = await importPKCS8(this.serviceAccount.private_key, 'RS256');
    const assertion = await new SignJWT({
      scope: 'https://www.googleapis.com/auth/androidpublisher',
    })
      .setProtectedHeader({ alg: 'RS256' })
      .setIssuer(this.serviceAccount.client_email)
      .setAudience(this.urls.token)
      .setIssuedAt()
      .setExpirationTime('1h')
      .sign(key);
    const res = await this.http(this.urls.token, {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({
        grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer',
        assertion,
      }),
    });
    if (!res.ok) throw new ApiError(503, 'PLATFORM_UNAVAILABLE', 'Google authentication failed');
    const json = (await res.json()) as { access_token: string; expires_in: number };
    this.token = { value: json.access_token, expires: Date.now() + json.expires_in * 1000 };
    return json.access_token;
  }

  private url(sku: string, token: string, suffix = ''): string {
    return (
      `${this.urls.api}/androidpublisher/v3/applications/${encodeURIComponent(this.packageName)}` +
      `/purchases/products/${encodeURIComponent(sku)}/tokens/${encodeURIComponent(token)}${suffix}`
    );
  }

  async verify(purchaseToken: string, expectedSku: string): Promise<VerifiedPurchase> {
    const auth = { authorization: `Bearer ${await this.accessToken()}` };
    const res = await this.http(this.url(expectedSku, purchaseToken), { headers: auth });
    if (res.status === 400 || res.status === 404)
      throw Rejected('Google does not recognise this purchase token');
    if (!res.ok)
      throw new ApiError(
        503,
        'PLATFORM_UNAVAILABLE',
        'Google Play verification is unavailable; try again',
      );
    const p = (await res.json()) as {
      purchaseState?: number;
      orderId?: string;
      purchaseType?: number;
      acknowledgementState?: number;
    };
    if (p.purchaseState !== 0) throw Rejected('Purchase is not in the purchased state');
    if (!p.orderId) throw Rejected('Purchase has no order id');
    return {
      providerTransactionId: p.orderId,
      sku: expectedSku,
      environment: p.purchaseType === 0 ? 'SANDBOX' : 'PRODUCTION', // purchaseType 0 = license test purchase
      tokenHash: sha256(purchaseToken),
      acknowledge:
        p.acknowledgementState === 1
          ? undefined
          : async () => {
              await this.http(this.url(expectedSku, purchaseToken, ':acknowledge'), {
                method: 'POST',
                headers: auth,
              });
            },
    };
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Apple: StoreKit 2 / App Store Server signed data (JWS with an x5c chain ending at the pinned Apple root).
// ---------------------------------------------------------------------------------------------------------------

/** Apple's marker extensions: leaf "Mac App Store receipt signing", intermediate "Apple WWDR". */
const APPLE_LEAF_OID = Buffer.from('060a2a864886f76364060b01', 'hex');
const APPLE_INTERMEDIATE_OID = Buffer.from('060a2a864886f76364060201', 'hex');

/** Verifies a JWS signed by Apple; returns its payload. Throws 422 on any problem. */
export async function verifyAppleJws<T>(
  jws: string,
  rootPem: string,
  now = new Date(),
): Promise<T> {
  const [headerB64] = jws.split('.');
  if (!headerB64) throw Rejected('Malformed signed data');
  let header: { alg?: string; x5c?: string[] };
  try {
    header = JSON.parse(Buffer.from(headerB64, 'base64url').toString('utf8'));
  } catch {
    throw Rejected('Malformed signed data header');
  }
  if (header.alg !== 'ES256' || !header.x5c || header.x5c.length !== 3)
    throw Rejected('Unexpected signature format');

  let leaf: X509Certificate, intermediate: X509Certificate, root: X509Certificate;
  try {
    [leaf, intermediate, root] = header.x5c.map(
      (b64) => new X509Certificate(Buffer.from(b64, 'base64')),
    ) as [X509Certificate, X509Certificate, X509Certificate];
  } catch {
    throw Rejected('Malformed certificate chain');
  }
  const trusted = new X509Certificate(rootPem);
  const valid = (c: X509Certificate) => new Date(c.validFrom) <= now && now <= new Date(c.validTo);
  if (!root.raw.equals(trusted.raw))
    throw Rejected('Certificate chain does not end at the trusted Apple root');
  if (!intermediate.verify(trusted.publicKey) || !leaf.verify(intermediate.publicKey))
    throw Rejected('Certificate chain signature is invalid');
  if (![leaf, intermediate, trusted].every(valid))
    throw Rejected('Certificate chain is expired or not yet valid');
  if (!leaf.raw.includes(APPLE_LEAF_OID) || !intermediate.raw.includes(APPLE_INTERMEDIATE_OID))
    throw Rejected('Certificates are not Apple App Store certificates');

  const key = await importX509(leaf.toString(), 'ES256');
  try {
    const { payload } = await compactVerify(jws, key);
    return JSON.parse(new TextDecoder().decode(payload)) as T;
  } catch {
    throw Rejected('Signed data signature is invalid');
  }
}

export interface AppleTransaction {
  transactionId: string;
  originalTransactionId: string;
  bundleId: string;
  productId: string;
  environment: 'Production' | 'Sandbox';
  revocationDate?: number;
}

export class AppleVerifier implements PurchaseVerifier {
  readonly platform = 'IOS' as const;

  constructor(
    private readonly bundleId: string,
    private readonly rootPem: string,
  ) {}

  async verify(signedTransaction: string, expectedSku: string): Promise<VerifiedPurchase> {
    const t = await verifyAppleJws<AppleTransaction>(signedTransaction, this.rootPem);
    if (t.bundleId !== this.bundleId) throw Rejected('Transaction is for another app');
    if (t.productId !== expectedSku) throw Rejected('Transaction is for a different product');
    if (t.revocationDate) throw Rejected('Transaction was revoked');
    return {
      providerTransactionId: t.transactionId,
      sku: t.productId,
      environment: t.environment === 'Sandbox' ? 'SANDBOX' : 'PRODUCTION',
      tokenHash: sha256(signedTransaction),
    };
  }
}
