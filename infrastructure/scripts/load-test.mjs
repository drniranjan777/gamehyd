// API load test (Phase 6): N virtual players sign in as guests, then loop over the calls a player makes from the
// menu and between matches (profile, inventory, catalogue, matchmaking status, telemetry). Reports throughput,
// latency percentiles per endpoint and the error rate. Node 22+, no dependencies.
//
//   node infrastructure/scripts/load-test.mjs --api http://localhost:3001/api/v1 --users 200 --seconds 60
import { randomUUID } from 'node:crypto';
import { parseArgs } from 'node:util';

const { values } = parseArgs({
  options: {
    api: { type: 'string', default: 'http://localhost:3001/api/v1' },
    users: { type: 'string', default: '100' },
    seconds: { type: 'string', default: '30' },
    'think-ms': { type: 'string', default: '200' },
    'p95-ms': { type: 'string', default: '300' },
  },
});
const API = values.api;
const USERS = Number(values.users);
const SECONDS = Number(values.seconds);
const THINK = Number(values['think-ms']);
const P95_TARGET = Number(values['p95-ms']);

const samples = new Map(); // endpoint -> latencies (ms)
let ok = 0;
let failed = 0;
const failures = new Map();

async function call(name, method, path, token, body) {
  const t0 = performance.now();
  try {
    const res = await fetch(API + path, {
      method,
      headers: {
        'content-type': 'application/json',
        ...(token ? { authorization: `Bearer ${token}` } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(10_000),
    });
    const json = res.status === 204 ? null : await res.json().catch(() => null);
    const ms = performance.now() - t0;
    if (!samples.has(name)) samples.set(name, []);
    samples.get(name).push(ms);
    if (res.ok) ok++;
    else {
      failed++;
      failures.set(`${name} ${res.status}`, (failures.get(`${name} ${res.status}`) ?? 0) + 1);
    }
    return json;
  } catch (err) {
    failed++;
    failures.set(`${name} ${err.name}`, (failures.get(`${name} ${err.name}`) ?? 0) + 1);
    return null;
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function player(i, until) {
  await sleep(Math.random() * 2000); // ramp up over 2 s
  const auth = await call('auth/guest', 'POST', '/auth/guest', null, { displayName: `Load_${i}` });
  if (!auth?.accessToken) return;
  const token = auth.accessToken;
  const session = randomUUID();
  await call('telemetry', 'POST', '/telemetry/events', token, {
    sessionId: session,
    appVersion: '0.6.0',
    platform: 'ANDROID',
    deviceModel: 'LoadTest',
    events: [{ type: 'SESSION_START', at: new Date().toISOString(), data: {} }],
  });
  while (Date.now() < until) {
    await call('player/profile', 'GET', '/player/profile', token);
    await call('inventory', 'GET', '/inventory', token);
    await call('store/catalog', 'GET', '/store/catalog', token);
    await call('matchmaking/status', 'GET', '/matchmaking/status', token);
    await call('player/matches', 'GET', '/player/matches?limit=10', token);
    await call('telemetry', 'POST', '/telemetry/events', token, {
      sessionId: session,
      appVersion: '0.6.0',
      platform: 'ANDROID',
      deviceModel: 'LoadTest',
      events: [
        {
          type: 'FPS_SAMPLE',
          at: new Date().toISOString(),
          data: { avgFps: 55, p5Fps: 40, seconds: 30 },
        },
      ],
    });
    await sleep(THINK);
  }
}

const pct = (sorted, p) =>
  sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))] ?? 0;

const started = Date.now();
const until = started + SECONDS * 1000;
await Promise.all(Array.from({ length: USERS }, (_, i) => player(i, until)));
const elapsed = (Date.now() - started) / 1000;

const all = [...samples.values()].flat().sort((a, b) => a - b);
console.log(
  `users ${USERS}, ${elapsed.toFixed(1)} s, ${ok + failed} requests, ${((ok + failed) / elapsed).toFixed(0)} req/s, errors ${failed} (${((100 * failed) / Math.max(1, ok + failed)).toFixed(2)}%)`,
);
console.log(
  'endpoint'.padEnd(20),
  'count'.padStart(7),
  'p50'.padStart(7),
  'p95'.padStart(7),
  'p99'.padStart(7),
  'max'.padStart(7),
);
for (const [name, list] of [...samples.entries()].sort()) {
  list.sort((a, b) => a - b);
  console.log(
    name.padEnd(20),
    String(list.length).padStart(7),
    ...[50, 95, 99].map((p) => pct(list, p).toFixed(0).padStart(7)),
    list[list.length - 1].toFixed(0).padStart(7),
  );
}
console.log(
  'all'.padEnd(20),
  String(all.length).padStart(7),
  ...[50, 95, 99].map((p) => pct(all, p).toFixed(0).padStart(7)),
);
for (const [k, n] of failures) console.log(`  failure: ${k} x${n}`);

const p95 = pct(all, 95);
const errorRate = failed / Math.max(1, ok + failed);
const pass = p95 <= P95_TARGET && errorRate <= 0.001;
console.log(
  pass
    ? `LOAD PASSED (p95 ${p95.toFixed(0)} ms <= ${P95_TARGET} ms, errors <= 0.1%)`
    : `LOAD FAILED (p95 ${p95.toFixed(0)} ms, errors ${(errorRate * 100).toFixed(2)}%)`,
);
process.exit(pass ? 0 : 1);
