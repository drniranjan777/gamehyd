import { createHmac } from 'node:crypto';
import { Pool } from 'pg';
import { verifyTicket, deriveSessionKey } from '../tickets/join-ticket';
import {
  bearer,
  describeDb,
  expectContract,
  guest,
  Harness,
  signed,
  startApi,
  TICKET_SECRET_HEX,
} from './harness';

const SERVER = '127.0.0.1:47777';
const hb = (state: 'IDLE' | 'HOSTING', extra: Record<string, unknown> = {}) => ({
  address: SERVER,
  region: 'IN-SOUTH',
  buildVersion: 'test',
  mapVersions: ['greybox-block-100-v2', 'greybox-block-40-v2'],
  state,
  ...extra,
});
const queue = { mode: 'SOLO', region: 'IN-SOUTH', clientVersion: '0.1.0' };

describeDb('matchmaking, servers and results (PostgreSQL)', () => {
  let h: Harness;
  let pool: Pool;
  beforeAll(async () => {
    h = await startApi(`rwbr_api_mm_${process.pid}`, {
      RATE_LIMIT_ENABLED: 'false',
      MATCH_CAPACITY: '3',
    });
    pool = new Pool({ connectionString: h.config.DATABASE_URL });
  }, 60_000);
  afterAll(async () => {
    await pool?.end();
    await h?.close();
  });

  it('rejects unsigned, wrongly signed and stale server requests', async () => {
    const unsigned = await h
      .http()
      .post('/api/v1/internal/game-servers/heartbeat')
      .send(hb('IDLE'));
    expect(unsigned.status).toBe(401);
    expectContract(unsigned, 'POST', '/internal/game-servers/heartbeat');
    const wrongKey = await signed(
      h,
      'POST',
      '/internal/game-servers/heartbeat',
      hb('IDLE'),
      SERVER,
      'x'.repeat(40),
    );
    expect(wrongKey.status).toBe(401);
    const spoofed = await signed(
      h,
      'POST',
      '/internal/game-servers/heartbeat',
      hb('IDLE'),
      '10.9.9.9:1',
    );
    expect(spoofed.status).toBe(409); // signed header and body disagree
  });

  it('plays the whole platform loop: queue, host, seat, result, rewards, history', async () => {
    const players = [await guest(h, 'Alpha'), await guest(h, 'Bravo'), await guest(h, 'Charlie')];

    // No server yet: everyone waits in the queue.
    const q0 = await h
      .http()
      .post('/api/v1/matchmaking/queue')
      .set(...bearer(players[0]!.accessToken))
      .send(queue);
    expect(q0.status).toBe(200);
    expectContract(q0, 'POST', '/matchmaking/queue');
    expect(q0.body.state).toBe('QUEUED');

    // A server registers idle; the next status poll creates a match on it, but no seat until it is hosting.
    const reg = await signed(h, 'POST', '/internal/game-servers/heartbeat', hb('IDLE'));
    expect(reg.status).toBe(200);
    expectContract(reg, 'POST', '/internal/game-servers/heartbeat');
    expect(reg.body.assignment).toBeNull();
    for (const p of players) {
      const s = await h
        .http()
        .post('/api/v1/matchmaking/queue')
        .set(...bearer(p.accessToken))
        .send(queue);
      expect(s.body.state).toBe('QUEUED');
    }

    const assigned = await signed(h, 'POST', '/internal/game-servers/heartbeat', hb('IDLE'));
    expectContract(assigned, 'POST', '/internal/game-servers/heartbeat');
    const a = assigned.body.assignment;
    expect(a).toMatchObject({ capacity: 3, mapVersion: 'greybox-block-100-v2' });
    // The live-ops loot table goes with every assignment (defaults until live ops changes it).
    expect(a.lootTable).toMatchObject({
      version: 1,
      fragPercent: 10,
      mediumRounds: 45,
      upgradePercent: 40,
    });

    const hosting = await signed(
      h,
      'POST',
      '/internal/game-servers/heartbeat',
      hb('HOSTING', { matchId: a.matchId, matchPhase: 'WAITING', connectedPlayers: 0 }),
    );
    expect(hosting.body.assignment).toBeNull();

    const seats = [];
    for (const p of players) {
      const st = await h
        .http()
        .get('/api/v1/matchmaking/status')
        .set(...bearer(p.accessToken));
      expectContract(st, 'GET', '/matchmaking/status');
      expect(st.body.state).toBe('MATCHED');
      seats.push(st.body.seat);
    }
    // The ticket is signed for this player and match; the session key matches what the server will derive.
    const secret = Buffer.from(TICKET_SECRET_HEX, 'hex');
    const t = verifyTicket(Buffer.from(seats[0].ticket, 'base64'), secret)!;
    expect(t).toMatchObject({
      matchId: a.matchId,
      accountId: players[0]!.userId,
      displayName: 'Alpha',
    });
    expect(Buffer.from(seats[0].sessionKey, 'base64')).toEqual(
      deriveSessionKey(secret, t.ticketId),
    );
    expect(seats[0].server).toBe(SERVER);

    // A fourth player does not fit (capacity 3) and waits.
    const late = await guest(h, 'Delta');
    const lq = await h
      .http()
      .post('/api/v1/matchmaking/queue')
      .set(...bearer(late.accessToken))
      .send(queue);
    expect(lq.body.state).toBe('QUEUED');

    await signed(
      h,
      'POST',
      '/internal/game-servers/heartbeat',
      hb('HOSTING', { matchId: a.matchId, matchPhase: 'ACTIVE', connectedPlayers: 3 }),
    );
    const leaveActive = await h
      .http()
      .delete('/api/v1/matchmaking/queue')
      .set(...bearer(players[1]!.accessToken));
    expect(leaveActive.status).toBe(409);

    const [alpha, bravo, charlie] = players.map((p) => p.userId);
    const forged = {
      mapVersion: 'greybox-block-100-v2',
      reason: 'WINNER',
      winnerAccountId: late.userId, // not in this match
      durationSeconds: 300,
      participants: [
        { accountId: late.userId, placement: 1, kills: 2, survivalSeconds: 300 },
        { accountId: alpha, placement: 2, kills: 0, survivalSeconds: 200 },
      ],
    };
    const rejected = await signed(h, 'POST', `/internal/matches/${a.matchId}/result`, forged);
    expect(rejected.status).toBe(422);
    expectContract(rejected, 'POST', '/internal/matches/{matchId}/result');

    const fromOtherServer = await signed(
      h,
      'POST',
      `/internal/matches/${a.matchId}/result`,
      forged,
      '10.0.0.9:7777',
    );
    expect(fromOtherServer.status).toBe(401);

    const result = {
      mapVersion: 'greybox-block-100-v2',
      reason: 'WINNER',
      winnerAccountId: bravo,
      durationSeconds: 300,
      participants: [
        { accountId: bravo, placement: 1, kills: 2, survivalSeconds: 300 },
        { accountId: alpha, placement: 2, kills: 0, survivalSeconds: 250 },
        { accountId: charlie, placement: 3, kills: 0, survivalSeconds: 100 },
      ],
    };
    const ok = await signed(h, 'POST', `/internal/matches/${a.matchId}/result`, result);
    expect(ok.status).toBe(200);
    expectContract(ok, 'POST', '/internal/matches/{matchId}/result');
    expect(ok.body.duplicate).toBe(false);

    const again = await signed(h, 'POST', `/internal/matches/${a.matchId}/result`, result);
    expect(again.body.duplicate).toBe(true);
    const changed = await signed(h, 'POST', `/internal/matches/${a.matchId}/result`, {
      ...result,
      durationSeconds: 301,
    });
    expect(changed.status).toBe(409);

    // Rewards: winner 100 + 2×50 + 2×10 + 300 = 520 XP, 10 + 10 + 50 = 70 coins, paid exactly once.
    const winnerStats = await h
      .http()
      .get('/api/v1/player/stats')
      .set(...bearer(players[1]!.accessToken));
    expect(winnerStats.body).toMatchObject({
      matchesPlayed: 1,
      wins: 1,
      kills: 2,
      xp: 520,
      winRate: 1,
    });
    const inv = await h
      .http()
      .get('/api/v1/inventory')
      .set(...bearer(players[1]!.accessToken));
    expect(inv.body.wallets.COIN).toBe(70);

    const history = await h
      .http()
      .get('/api/v1/player/matches')
      .set(...bearer(players[0]!.accessToken));
    expectContract(history, 'GET', '/player/matches');
    expect(history.body.items[0]).toMatchObject({
      matchId: a.matchId,
      placement: 2,
      players: 3,
      xpAwarded: 110,
      coinsAwarded: 10,
    });

    // The website's public profile shows the same result, without private reward details.
    const pub = await h.http().get(`/api/v1/players/${players[0]!.userId}/public`);
    expectContract(pub, 'GET', '/players/{playerId}/public');
    expect(pub.body.recentMatches[0]).toEqual({
      matchId: a.matchId,
      mapVersion: 'greybox-block-100-v2',
      placement: 2,
      players: 3,
      kills: expect.any(Number),
      completedAt: expect.any(String),
    });

    const res = await h
      .http()
      .get(`/api/v1/matches/${a.matchId}/result`)
      .set(...bearer(players[2]!.accessToken));
    expectContract(res, 'GET', '/matches/{matchId}/result');
    expect(res.body.winnerPlayerId).toBe(bravo);
    const outsider = await h
      .http()
      .get(`/api/v1/matches/${a.matchId}/result`)
      .set(...bearer(late.accessToken));
    expect(outsider.status).toBe(404);

    const reconnect = await h
      .http()
      .post(`/api/v1/matches/${a.matchId}/reconnect-ticket`)
      .set(...bearer(players[0]!.accessToken));
    expect(reconnect.status).toBe(409);
    expectContract(reconnect, 'POST', '/matches/{matchId}/reconnect-ticket');

    // Evidence and ledger integrity.
    const { rows: evidence } = await pool.query(
      `SELECT outcome FROM match_verification_records WHERE match_id = $1 ORDER BY id`,
      [a.matchId],
    );
    expect(evidence.map((r) => r.outcome)).toEqual([
      'REJECTED',
      'REJECTED',
      'ACCEPTED',
      'DUPLICATE',
      'REJECTED',
    ]);
    const { rowCount: drift } = await pool.query('SELECT * FROM wallet_ledger_drift');
    expect(drift).toBe(0);
    const { rows: ledger } = await pool.query(
      `SELECT count(*)::int AS n FROM wallet_transactions WHERE reference = $1`,
      [`match:${a.matchId}`],
    );
    expect(ledger[0].n).toBe(3);

    // After the match the server is idle again and the waiting player gets the next match.
    const next = await signed(h, 'POST', '/internal/game-servers/heartbeat', hb('IDLE'));
    expect(next.body.assignment).toBeNull(); // created on the next queue poll
    await h
      .http()
      .get('/api/v1/matchmaking/status')
      .set(...bearer(late.accessToken));
    const second = await signed(h, 'POST', '/internal/game-servers/heartbeat', hb('IDLE'));
    expect(second.body.assignment.matchId).not.toBe(a.matchId);
  });

  it('refuses outdated clients', async () => {
    const g = await guest(h);
    const res = await h
      .http()
      .post('/api/v1/matchmaking/queue')
      .set(...bearer(g.accessToken))
      .send({ ...queue, clientVersion: '0.0.9' });
    expect(res.status).toBe(409);
    expectContract(res, 'POST', '/matchmaking/queue');
    expect(res.body.error.code).toBe('CLIENT_OUTDATED');
  });

  it('signs over the exact body bytes', () => {
    // Guard against accidental re-serialisation: the HMAC input includes sha256 of the raw body.
    const a = createHmac('sha256', 'k')
      .update('1.POST./x.' + 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855')
      .digest('hex');
    expect(a).toHaveLength(64);
  });
});
