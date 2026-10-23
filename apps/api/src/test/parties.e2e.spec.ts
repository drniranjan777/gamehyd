import { verifyTicket } from '../tickets/join-ticket';
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

const SERVER = '127.0.0.1:47888';
const hb = (state: 'IDLE' | 'HOSTING', extra: Record<string, unknown> = {}) => ({
  address: SERVER,
  region: 'IN-SOUTH',
  buildVersion: 'test',
  mapVersions: ['greybox-block-100-v2', 'greybox-block-40-v2'],
  state,
  ...extra,
});
const duo = { mode: 'DUO', region: 'IN-SOUTH', clientVersion: '0.1.0' };

/**
 * Phase C part 6c: parties and Duo/Squad. A leader creates a party, a friend joins with the code, the leader queues
 * for both; matchmaking puts the party in one match and one team, teams up solo players, the seat tickets name the
 * team, and results are ranked and rewarded per team.
 */
describeDb('parties and team matchmaking (PostgreSQL)', () => {
  let h: Harness;
  beforeAll(async () => {
    h = await startApi(`rwbr_api_parties_${process.pid}`, {
      RATE_LIMIT_ENABLED: 'false',
      MATCH_CAPACITY: '4',
    });
  }, 60_000);
  afterAll(() => h?.close());

  const api = () => h.http();

  it('creates, joins, limits and leaves parties', async () => {
    const [a, b, c] = [await guest(h, 'Lead'), await guest(h, 'Friend'), await guest(h, 'Third')];
    const created = await api()
      .post('/api/v1/parties')
      .set(...bearer(a.accessToken))
      .send({ mode: 'DUO' });
    expect(created.status).toBe(201);
    expectContract(created, 'POST', '/parties');
    const code: string = created.body.code;
    expect(code).toMatch(/^[A-HJ-NP-Z2-9]{6}$/);

    const joined = await api()
      .post('/api/v1/parties/join')
      .set(...bearer(b.accessToken))
      .send({ code: code.toLowerCase() });
    expect(joined.status).toBe(200);
    expectContract(joined, 'POST', '/parties/join');
    expect(joined.body.members.map((m: { displayName: string }) => m.displayName)).toEqual([
      'Lead',
      'Friend',
    ]);

    const full = await api()
      .post('/api/v1/parties/join')
      .set(...bearer(c.accessToken))
      .send({ code });
    expect(full.status).toBe(409);
    expect(full.body.error.code).toBe('PARTY_FULL');

    // Only the leader changes the mode; Squad makes room for the third.
    const notLeader = await api()
      .put('/api/v1/parties/current/mode')
      .set(...bearer(b.accessToken))
      .send({ mode: 'SQUAD' });
    expect(notLeader.status).toBe(403);
    const squad = await api()
      .put('/api/v1/parties/current/mode')
      .set(...bearer(a.accessToken))
      .send({ mode: 'SQUAD' });
    expect(squad.body.mode).toBe('SQUAD');
    expect(
      (
        await api()
          .post('/api/v1/parties/join')
          .set(...bearer(c.accessToken))
          .send({ code })
      ).status,
    ).toBe(200);
    // Back to Duo does not fit three.
    const shrink = await api()
      .put('/api/v1/parties/current/mode')
      .set(...bearer(a.accessToken))
      .send({ mode: 'DUO' });
    expect(shrink.status).toBe(409);

    // The leader leaves: the next to join leads; when everyone leaves the party is gone.
    expect(
      (
        await api()
          .delete('/api/v1/parties/current')
          .set(...bearer(a.accessToken))
      ).status,
    ).toBe(204);
    const now = await api()
      .get('/api/v1/parties/current')
      .set(...bearer(c.accessToken));
    expectContract(now, 'GET', '/parties/current');
    expect(now.body.party.leaderId).toBe(b.userId);
    expect(now.body.party.members).toHaveLength(2);
    await api()
      .delete('/api/v1/parties/current')
      .set(...bearer(b.accessToken));
    await api()
      .delete('/api/v1/parties/current')
      .set(...bearer(c.accessToken));
    const gone = await api()
      .post('/api/v1/parties/join')
      .set(...bearer(a.accessToken))
      .send({ code });
    expect(gone.status).toBe(404);
  });

  it('keeps a party in one team, teams up solo players, and ranks results per team', async () => {
    const [lead, friend, solo1, solo2] = [
      await guest(h, 'Lead2'),
      await guest(h, 'Friend2'),
      await guest(h, 'Solo1'),
      await guest(h, 'Solo2'),
    ];
    const party = await api()
      .post('/api/v1/parties')
      .set(...bearer(lead.accessToken))
      .send({});
    await api()
      .post('/api/v1/parties/join')
      .set(...bearer(friend.accessToken))
      .send({ code: party.body.code });

    // Members do not queue themselves, and a party cannot play Solo.
    const member = await api()
      .post('/api/v1/matchmaking/queue')
      .set(...bearer(friend.accessToken))
      .send(duo);
    expect(member.status).toBe(409);
    const solo = await api()
      .post('/api/v1/matchmaking/queue')
      .set(...bearer(lead.accessToken))
      .send({ ...duo, mode: 'SOLO' });
    expect(solo.status).toBe(409);

    await signed(h, 'POST', '/internal/game-servers/heartbeat', hb('IDLE'), SERVER);
    const q = await api()
      .post('/api/v1/matchmaking/queue')
      .set(...bearer(lead.accessToken))
      .send(duo);
    expect(q.status).toBe(200);
    // The leader queued both: the friend is placed too.
    const friendStatus = await api()
      .get('/api/v1/matchmaking/status')
      .set(...bearer(friend.accessToken));
    expect(friendStatus.body.state).toBe('QUEUED');
    for (const p of [solo1, solo2])
      await api()
        .post('/api/v1/matchmaking/queue')
        .set(...bearer(p.accessToken))
        .send(duo);

    const assigned = await signed(
      h,
      'POST',
      '/internal/game-servers/heartbeat',
      hb('IDLE'),
      SERVER,
    );
    expectContract(assigned, 'POST', '/internal/game-servers/heartbeat');
    const a = assigned.body.assignment;
    expect(a).toMatchObject({ capacity: 4, teamSize: 2 });
    await signed(
      h,
      'POST',
      '/internal/game-servers/heartbeat',
      hb('HOSTING', { matchId: a.matchId, matchPhase: 'WAITING', connectedPlayers: 0 }),
      SERVER,
    );

    const secret = Buffer.from(TICKET_SECRET_HEX, 'hex');
    const team: Record<string, number> = {};
    for (const p of [lead, friend, solo1, solo2]) {
      const st = await api()
        .get('/api/v1/matchmaking/status')
        .set(...bearer(p.accessToken));
      expect(st.body.state).toBe('MATCHED');
      expect(st.body.seat.matchId).toBe(a.matchId);
      team[p.userId] = verifyTicket(Buffer.from(st.body.seat.ticket, 'base64'), secret)!.team;
    }
    expect(team[lead.userId]).toBe(team[friend.userId]);
    expect(team[solo1.userId]).toBe(team[solo2.userId]);
    expect(team[solo1.userId]).not.toBe(team[lead.userId]);

    const submit = (participants: object[], extra: object = {}) =>
      signed(
        h,
        'POST',
        `/internal/matches/${a.matchId}/result`,
        {
          mapVersion: a.mapVersion,
          reason: 'WINNER',
          winnerAccountId: friend.userId,
          durationSeconds: 300,
          teams: 2,
          participants,
          ...extra,
        },
        SERVER,
      );
    const row = (p: { userId: string }, placement: number, t = team[p.userId]) => ({
      accountId: p.userId,
      placement,
      team: t,
      kills: 0,
      survivalSeconds: 200,
    });

    // Teammates with different places, two teams in one place, or a made-up team are refused.
    const split = await submit([row(lead, 1), row(friend, 2), row(solo1, 2), row(solo2, 2)]);
    expect(split.status).toBe(422);
    const shared = await submit([row(lead, 1), row(friend, 1), row(solo1, 1), row(solo2, 1)]);
    expect(shared.status).toBe(422);
    const wrongTeam = await submit([
      row(lead, 1),
      row(friend, 1, team[solo1.userId]),
      row(solo1, 2),
      row(solo2, 2),
    ]);
    expect(wrongTeam.status).toBe(422);

    const ok = await submit([row(lead, 1), row(friend, 1), row(solo1, 2), row(solo2, 2)]);
    expect(ok.status).toBe(200);
    // The whole winning team wins; rewards rank over the two teams.
    for (const [p, wins] of [
      [lead, 1],
      [friend, 1],
      [solo1, 0],
    ] as const) {
      const stats = await api()
        .get('/api/v1/player/stats')
        .set(...bearer(p.accessToken));
      expect(stats.body.wins).toBe(wins);
    }
  });
});
