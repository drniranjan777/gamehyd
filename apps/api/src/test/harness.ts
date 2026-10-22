import { INestApplication } from '@nestjs/common';
import { Test } from '@nestjs/testing';
import Ajv2020 from 'ajv/dist/2020';
import addFormats from 'ajv-formats';
import { execFileSync } from 'node:child_process';
import { createHmac } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import request, { Response } from 'supertest';
import { parse as parseYaml } from 'yaml';
import { AppModule } from '../app.module';
import { configureApp } from '../app.setup';
import { signingPayload } from '../common/guards';
import { AppConfig, loadConfig } from '../config';

export const adminUrl = process.env.TEST_ADMIN_DATABASE_URL;
/** Use as `describeDb(...)`: integration suites need PostgreSQL and skip without it. */
export const describeDb = adminUrl ? describe : describe.skip;

const repoRoot = path.resolve(__dirname, '..', '..', '..', '..');
const databaseDir = path.join(repoRoot, 'database');

function dbCli(command: 'create' | 'drop', name: string): string {
  return execFileSync(process.execPath, ['--import', 'tsx', 'src/testdb-cli.ts', command, name], {
    cwd: databaseDir,
    env: { ...process.env, TEST_ADMIN_DATABASE_URL: adminUrl },
    encoding: 'utf8',
  });
}

export const SERVER_SECRET = 'test-server-secret-0123456789abcdef0123456789';
export const TICKET_SECRET_HEX = 'a1'.repeat(32);

export interface Harness {
  app: INestApplication;
  config: AppConfig;
  http: () => ReturnType<typeof request>;
  close(): Promise<void>;
}

export async function startApi(
  dbName: string,
  overrides: Record<string, string> = {},
): Promise<Harness> {
  const { appUrl } = JSON.parse(dbCli('create', dbName)) as { appUrl: string };
  const config = loadConfig({
    DATABASE_URL: appUrl,
    JWT_SECRET: 'test-jwt-secret-0123456789abcdef0123456789abcdef',
    SERVER_SHARED_SECRET: SERVER_SECRET,
    TICKET_SECRET_HEX,
    LOG_REQUESTS: 'false',
    MATCH_CAPACITY: '3',
    DEFAULT_MAP_VERSION: 'greybox-block-100-v2',
    ...overrides,
  });
  const moduleRef = await Test.createTestingModule({
    imports: [AppModule.forConfig(config)],
  }).compile();
  const app = moduleRef.createNestApplication({ rawBody: true, logger: false });
  configureApp(app, config);
  await app.init();
  return {
    app,
    config,
    http: () => request(app.getHttpServer()),
    async close() {
      await app.close();
      dbCli('drop', dbName);
    },
  };
}

// ---- Contract validation against packages/api-contracts/openapi.yaml ----

const spec = parseYaml(
  readFileSync(path.join(repoRoot, 'packages', 'api-contracts', 'openapi.yaml'), 'utf8'),
);
const ajv = new Ajv2020({ strict: false, allErrors: true });
addFormats(ajv);
const SPEC_ID = 'https://rwbr.local/openapi.json';
ajv.addSchema({ ...spec, $id: SPEC_ID });

/** Asserts the response body matches the spec for this operation and status. */
export function expectContract(res: Response, method: string, pathTemplate: string): void {
  const op = spec.paths?.[pathTemplate]?.[method.toLowerCase()];
  if (!op) throw new Error(`No operation ${method} ${pathTemplate} in openapi.yaml`);
  let response = op.responses?.[String(res.status)];
  if (!response)
    throw new Error(
      `${method} ${pathTemplate} returned ${res.status}, which the spec does not declare`,
    );
  if (response.$ref) response = resolve(response.$ref);
  const schema = response.content?.['application/json']?.schema;
  if (!schema) {
    expect(res.text === '' || res.text === undefined).toBe(true);
    return;
  }
  const validate = schema.$ref ? ajv.getSchema(`${SPEC_ID}${schema.$ref}`)! : ajv.compile(schema);
  if (!validate(res.body)) {
    throw new Error(
      `${method} ${pathTemplate} ${res.status} violates the contract: ${ajv.errorsText(validate.errors)}\n${JSON.stringify(res.body)}`,
    );
  }
}

function resolve(ref: string): any {
  return ref
    .replace(/^#\//, '')
    .split('/')
    .reduce((node: any, key) => node[key], spec);
}

/** Inline schemas in the spec may $ref components; register a resolver that rewrites them. */
ajv.addKeyword({ keyword: 'x-internal', schemaType: 'boolean' });
const originalCompile = ajv.compile.bind(ajv);
(ajv as any).compile = (schema: any) => originalCompile(rewriteRefs(schema));
function rewriteRefs(node: any): any {
  if (Array.isArray(node)) return node.map(rewriteRefs);
  if (node && typeof node === 'object') {
    const out: any = {};
    for (const [k, v] of Object.entries(node)) {
      if (v === undefined) continue;
      out[k] =
        k === '$ref' && typeof v === 'string' && v.startsWith('#/')
          ? `${SPEC_ID}${v}`
          : rewriteRefs(v);
    }
    return out;
  }
  return node;
}

// ---- Helpers ----

export async function guest(h: Harness, displayName?: string) {
  const res = await h
    .http()
    .post('/api/v1/auth/guest')
    .send(displayName ? { displayName } : {});
  expect(res.status).toBe(200);
  expectContract(res, 'POST', '/auth/guest');
  return res.body as {
    userId: string;
    accessToken: string;
    refreshToken: string;
    guest: { deviceId: string; deviceSecret: string };
  };
}

export function bearer(token: string): [string, string] {
  return ['Authorization', `Bearer ${token}`];
}

/** Sends a correctly signed /internal request as `server`. */
export function signed(
  h: Harness,
  method: 'POST',
  urlPath: string,
  body: unknown,
  server = '127.0.0.1:47777',
  secret = SERVER_SECRET,
) {
  const raw = Buffer.from(JSON.stringify(body));
  const ts = String(Math.floor(Date.now() / 1000));
  const sig = createHmac('sha256', secret)
    .update(signingPayload(ts, method, `/api/v1${urlPath}`, raw))
    .digest('hex');
  return h
    .http()
    .post(`/api/v1${urlPath}`)
    .set('Content-Type', 'application/json')
    .set('X-RWBR-Server', server)
    .set('X-RWBR-Timestamp', ts)
    .set('X-RWBR-Signature', sig)
    .send(raw.toString());
}
