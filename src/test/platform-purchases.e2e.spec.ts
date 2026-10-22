import * as x509 from '@peculiar/x509';
import { CompactSign, exportJWK, SignJWT } from 'jose';
import { generateKeyPairSync, KeyObject, webcrypto } from 'node:crypto';
import { createServer, IncomingMessage, Server, ServerResponse } from 'node:http';
import { AddressInfo } from 'node:net';
import { bearer, describeDb, expectContract, guest, Harness, startApi } from './harness';

x509.cryptoProvider.set(webcrypto as unknown as Crypto);

const PACKAGE = 'com.rwbr.test';
const BUNDLE = 'com.rwbr.test.ios';
const RTDN_AUDIENCE = 'https://api.rwbr.test/webhooks/google-play';
const RTDN_ACCOUNT = 'rtdn-push@rwbr-test.iam.gserviceaccount.com';

// ---- A local stand-in for Google's token, Android Publisher and JWKS endpoints ----

interface MockGoogle {
  url: string;
  purchases: Map<string, object>;
  acknowledged: string[];
  rtdnKey: KeyObject;
  serviceAccountJson: string;
  close(): Promise<void>;
}

async function startMockGoogle(): Promise<MockGoogle> {
  const account = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const rtdn = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const jwk = { ...(await exportJWK(rtdn.publicKey)), kid: 'k1', alg: 'RS256', use: 'sig' };
  const purchases = new Map<string, object>();
  const acknowledged: string[] = [];
  const server: Server = createServer((req: IncomingMessage, res: ServerResponse) => {
    const send = (status: number, body?: object) => {
      res.writeHead(status, { 'content-type': 'application/json' });
      res.end(body ? JSON.stringify(body) : undefined);
    };
    const path = decodeURIComponent(req.url ?? '');
    if (req.method === 'POST' && path === '/token')
      return send(200, { access_token: 'mock-access', expires_in: 3600 });
    if (path === '/certs') return send(200, { keys: [jwk] });
    if (req.headers.authorization !== 'Bearer mock-access') return send(401, {});
    const m =
      /^\/androidpublisher\/v3\/applications\/([^/]+)\/purchases\/products\/([^/]+)\/tokens\/([^/:]+)(:acknowledge)?$/.exec(
        path,
      );
    if (!m || m[1] !== PACKAGE) return send(404, {});
    const token = m[3]!;
    if (token === 'tok-outage') return send(500, {});
    if (m[4]) {
      acknowledged.push(token);
      return send(204);
    }
    const p = purchases.get(token);
    return p ? send(200, p) : send(404, {});
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  return {
    url,
    purchases,
    acknowledged,
    rtdnKey: rtdn.privateKey,
    serviceAccountJson: JSON.stringify({
      client_email: 'verifier@rwbr-test.iam.gserviceaccount.com',
      private_key: account.privateKey.export({ type: 'pkcs8', format: 'pem' }),
    }),
    close: () => new Promise((resolve) => server.close(() => resolve())),
  };
}

// ---- An Apple-shaped certificate chain (root -> WWDR-like intermediate -> signing leaf) ----

const EC = { name: 'ECDSA', namedCurve: 'P-256', hash: 'SHA-256' };
const NULL_VALUE = new Uint8Array([5, 0]);

interface Chain {
  rootPem: string;
  x5c: string[];
  leafKey: CryptoKey;
  root: { cert: x509.X509Certificate; keys: CryptoKeyPair };
}

/** A fresh chain; pass `under` to issue it from an existing root (to vary only the Apple marker extensions). */
async function appleChain(withMarkers = true, under?: Chain['root']): Promise<Chain> {
  const notBefore = new Date(Date.now() - 86400_000);
  const notAfter = new Date(Date.now() + 365 * 86400_000);
  const keys = () =>
    webcrypto.subtle.generateKey(EC, true, ['sign', 'verify']) as Promise<CryptoKeyPair>;
  const [ownRootKeys, intKeys, leafKeys] = await Promise.all([keys(), keys(), keys()]);
  const rootKeys = under?.keys ?? ownRootKeys;
  const root =
    under?.cert ??
    (await x509.X509CertificateGenerator.createSelfSigned({
      serialNumber: '01',
      name: 'CN=Test Apple Root CA - G3',
      notBefore,
      notAfter,
      keys: rootKeys,
      signingAlgorithm: EC,
      extensions: [new x509.BasicConstraintsExtension(true, undefined, true)],
    }));
  const intermediate = await x509.X509CertificateGenerator.create({
    serialNumber: '02',
    subject: 'CN=Test Apple Worldwide Developer Relations',
    issuer: root.subject,
    notBefore,
    notAfter,
    publicKey: intKeys.publicKey,
    signingKey: rootKeys.privateKey,
    signingAlgorithm: EC,
    extensions: [
      new x509.BasicConstraintsExtension(true, 0, true),
      ...(withMarkers ? [new x509.Extension('1.2.840.113635.100.6.2.1', false, NULL_VALUE)] : []),
    ],
  });
  const leaf = await x509.X509CertificateGenerator.create({
    serialNumber: '03',
    subject: 'CN=Test Prod ECC Mac App Store and iTunes Store Receipt Signing',
    issuer: intermediate.subject,
    notBefore,
    notAfter,
    publicKey: leafKeys.publicKey,
    signingKey: intKeys.privateKey,
    signingAlgorithm: EC,
    extensions: withMarkers
      ? [new x509.Extension('1.2.840.113635.100.6.11.1', false, NULL_VALUE)]
      : [],
  });
  return {
    rootPem: root.toString('pem'),
    x5c: [leaf, intermediate, root].map((c) => Buffer.from(c.rawData).toString('base64')),
    leafKey: leafKeys.privateKey,
    root: { cert: root, keys: rootKeys },
  };
}

const signApple = (payload: object, chain: Chain) =>
  new CompactSign(new TextEncoder().encode(JSON.stringify(payload)))
    .setProtectedHeader({ alg: 'ES256', x5c: chain.x5c })
    .sign(chain.leafKey);

describeDb('Google Play and App Store purchases (PostgreSQL, mocked platforms)', () => {
  let h: Harness;
  let google: MockGoogle;
  let apple: Chain;

  beforeAll(async () => {
    google = await startMockGoogle();
    apple = await appleChain();
    h = await startApi(`rwbr_api_platform_${process.pid}`, {
      RATE_LIMIT_ENABLED: 'false',
      GOOGLE_PLAY_PACKAGE_NAME: PACKAGE,
      GOOGLE_SERVICE_ACCOUNT_JSON: google.serviceAccountJson,
      GOOGLE_API_BASE_URL: google.url,
      GOOGLE_TOKEN_URL: `${google.url}/token`,
      GOOGLE_RTDN_JWKS_URL: `${google.url}/certs`,
      GOOGLE_RTDN_AUDIENCE: RTDN_AUDIENCE,
      GOOGLE_RTDN_SERVICE_ACCOUNT: RTDN_ACCOUNT,
      APPLE_BUNDLE_ID: BUNDLE,
      APPLE_ROOT_CERT_PEM: apple.rootPem,
    });
  }, 60_000);
  afterAll(async () => {
    await h?.close();
    await google?.close();
  });

  const api = () => h.http();
  const order = (token: string, productKey: string, platform: string, key: string) =>
    api()
      .post('/api/v1/store/orders')
      .set(...bearer(token))
      .send({ productKey, platform, idempotencyKey: key });
  const verify = (token: string, orderId: string, proof: string) =>
    api()
      .post(`/api/v1/store/orders/${orderId}/verify`)
      .set(...bearer(token))
      .send({ proof });
  const owns = async (token: string, key: string) =>
    (
      await api()
        .get('/api/v1/inventory')
        .set(...bearer(token))
    ).body.items.some((i: { cosmeticKey: string }) => i.cosmeticKey === key);

  async function rtdn(
    notification: object,
    messageId: string,
    signWith: KeyObject = google.rtdnKey,
    email = RTDN_ACCOUNT,
  ) {
    const jwt = await new SignJWT({ email, email_verified: true })
      .setProtectedHeader({ alg: 'RS256', kid: 'k1' })
      .setIssuer('https://accounts.google.com')
      .setAudience(RTDN_AUDIENCE)
      .setIssuedAt()
      .setExpirationTime('5m')
      .sign(signWith);
    const data = Buffer.from(
      JSON.stringify({ version: '1.0', packageName: PACKAGE, ...notification }),
    ).toString('base64');
    return api()
      .post('/api/v1/webhooks/google-play')
      .set('Authorization', `Bearer ${jwt}`)
      .send({ message: { data, messageId }, subscription: 'projects/rwbr/subscriptions/rtdn' });
  }

  it('verifies a Google Play purchase server-to-server and acknowledges it', async () => {
    const g = await guest(h);
    const o = await order(g.accessToken, 'skin_rifle_charminar_gold', 'ANDROID', 'gp-order-0001');
    expect(o.status).toBe(201);
    expect(o.body.sku).toBe('rwbr.skin_rifle_charminar_gold');

    google.purchases.set('tok-good-1', {
      purchaseState: 0,
      orderId: 'GPA.1111-0001',
      acknowledgementState: 0,
    });
    google.purchases.set('tok-cancelled', { purchaseState: 1, orderId: 'GPA.1111-0002' });

    const unknown = await verify(g.accessToken, o.body.orderId, 'tok-not-real');
    expect(unknown.status).toBe(422);
    const cancelled = await verify(g.accessToken, o.body.orderId, 'tok-cancelled');
    expect(cancelled.body.error.code).toBe('RECEIPT_REJECTED');
    const outage = await verify(g.accessToken, o.body.orderId, 'tok-outage');
    expect(outage.status).toBe(503);
    expectContract(outage, 'POST', '/store/orders/{orderId}/verify');

    const ok = await verify(g.accessToken, o.body.orderId, 'tok-good-1');
    expect(ok.status).toBe(200);
    expect(ok.body.status).toBe('FULFILLED');
    expect(google.acknowledged).toContain('tok-good-1');
    expect(await owns(g.accessToken, 'skin_rifle_charminar_gold')).toBe(true);
  });

  it('applies a voided-purchase notification once and rejects unsigned pushes', async () => {
    const g = await guest(h);
    const o = await order(g.accessToken, 'outfit_lakeside_runner', 'ANDROID', 'gp-order-0002');
    google.purchases.set('tok-good-2', {
      purchaseState: 0,
      orderId: 'GPA.2222-0001',
      acknowledgementState: 1,
    });
    expect((await verify(g.accessToken, o.body.orderId, 'tok-good-2')).body.status).toBe(
      'FULFILLED',
    );

    const voided = {
      voidedPurchaseNotification: {
        purchaseToken: 'tok-good-2',
        orderId: 'GPA.2222-0001',
        productType: 2,
        refundType: 1,
      },
    };
    const forger = generateKeyPairSync('rsa', { modulusLength: 2048 });
    const forged = await rtdn(voided, 'msg-forged', forger.privateKey);
    expect(forged.status).toBe(401);
    const wrongSender = await rtdn(
      voided,
      'msg-wrong-sender',
      google.rtdnKey,
      'someone@example.com',
    );
    expect(wrongSender.status).toBe(401);

    const first = await rtdn(voided, 'msg-1');
    expect(first.status).toBe(200);
    expectContract(first, 'POST', '/webhooks/google-play');
    expect(first.body.outcome).toBe('APPLIED');
    expect((await rtdn(voided, 'msg-1')).body.outcome).toBe('APPLIED');
    expect(await owns(g.accessToken, 'outfit_lakeside_runner')).toBe(false);

    const test = await rtdn({ testNotification: { version: '1.0' } }, 'msg-test');
    expect(test.body.outcome).toBe('IGNORED');
  });

  it('verifies a StoreKit 2 transaction against the pinned root', async () => {
    const g = await guest(h);
    const o = await order(g.accessToken, 'outfit_lakeside_runner', 'IOS', 'ios-order-0001');
    expect(o.body.sku).toBe('rwbr.outfit_lakeside_runner');
    const tx = (over: object = {}) => ({
      transactionId: '2000000000000001',
      originalTransactionId: '2000000000000001',
      bundleId: BUNDLE,
      productId: 'rwbr.outfit_lakeside_runner',
      environment: 'Sandbox',
      ...over,
    });

    const otherApp = await verify(
      g.accessToken,
      o.body.orderId,
      await signApple(tx({ bundleId: 'com.evil.app' }), apple),
    );
    expect(otherApp.status).toBe(422);
    const selfMade = await appleChain();
    const untrusted = await verify(g.accessToken, o.body.orderId, await signApple(tx(), selfMade));
    expect(untrusted.status).toBe(422);
    // Valid chain to the trusted root, but not issued as App Store signing certificates.
    const unmarked = await verify(
      g.accessToken,
      o.body.orderId,
      await signApple(tx(), await appleChain(false, apple.root)),
    );
    expect(unmarked.status).toBe(422);
    expect(unmarked.body.error.message).toBe('Certificates are not Apple App Store certificates');
    const tampered = (await signApple(tx(), apple)).split('.');
    tampered[1] = Buffer.from(JSON.stringify(tx({ productId: 'rwbr.gems_100' }))).toString(
      'base64url',
    );
    expect((await verify(g.accessToken, o.body.orderId, tampered.join('.'))).status).toBe(422);

    const ok = await verify(g.accessToken, o.body.orderId, await signApple(tx(), apple));
    expect(ok.status).toBe(200);
    expect(ok.body.status).toBe('FULFILLED');
    expect(await owns(g.accessToken, 'outfit_lakeside_runner')).toBe(true);

    // App Store Server Notification: REFUND for that transaction.
    const notification = (uuid: string) =>
      signApple(
        {
          notificationType: 'REFUND',
          notificationUUID: uuid,
          data: { bundleId: BUNDLE, environment: 'Sandbox', signedTransactionInfo: '' },
        },
        apple,
      );
    const signedTransactionInfo = await signApple(tx({ revocationDate: Date.now() }), apple);
    const payload = await signApple(
      {
        notificationType: 'REFUND',
        notificationUUID: 'n-1',
        data: { bundleId: BUNDLE, environment: 'Sandbox', signedTransactionInfo },
      },
      apple,
    );
    const forgedHook = await api()
      .post('/api/v1/webhooks/app-store')
      .send({ signedPayload: await signApple({ notificationUUID: 'n-x' }, selfMade) });
    expect(forgedHook.status).toBe(401);
    const hook = await api().post('/api/v1/webhooks/app-store').send({ signedPayload: payload });
    expect(hook.status).toBe(200);
    expectContract(hook, 'POST', '/webhooks/app-store');
    expect(hook.body.outcome).toBe('APPLIED');
    expect(
      (await api().post('/api/v1/webhooks/app-store').send({ signedPayload: payload })).body
        .outcome,
    ).toBe('APPLIED');
    expect(await owns(g.accessToken, 'outfit_lakeside_runner')).toBe(false);
    const other = await api()
      .post('/api/v1/webhooks/app-store')
      .send({ signedPayload: await notification('n-2') });
    expect(other.body.outcome).toBe('REJECTED');
  });
});
