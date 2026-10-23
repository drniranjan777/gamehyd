import { Db } from '../database/database.module';
import { bearer, describeDb, guest, Harness, signed, startApi } from './harness';

/**
 * Phase C part 5: matches with fill bots. The match server lists only players, says how many bots played, and each
 * player keeps their place in the whole field (so 3rd behind two bots is 3rd), which also sets their reward.
 */
describeDb('results with fill bots (PostgreSQL)', () => {
  let h: Harness;
  let port = 47900;

  beforeAll(async () => {
    h = await startApi(`rwbr_api_bots_${process.pid}`, { RATE_LIMIT_ENABLED: 'false' });
  }, 60_000);
  afterAll(() => h?.close());

  /** A hosted match with two real players on a fresh server address. */
  async function match() {
    const db = h.app.get(Db);
    const address = `127.0.0.1:${port++}`;
    const server = await db.one<{ id: string }>(
      `INSERT INTO game_servers (address, region, build_version) VALUES ($1, 'IN-SOUTH', 't') RETURNING id`,
      [address],
    );
    const m = await db.one<{ id: string }>(
      `INSERT INTO matches (game_server_id, map_version, mode, region, capacity, ruleset_version, hosted_at)
       VALUES ($1, 'hyd-city-v1', 'SOLO', 'IN-SOUTH', 6, 'v1', now()) RETURNING id`,
      [server!.id],
    );
    const players = [await guest(h), await guest(h)] as const;
    for (const p of players)
      await db.query('INSERT INTO match_participants (match_id, user_id) VALUES ($1, $2)', [
        m!.id,
        p.userId,
      ]);
    return { id: m!.id, address, players };
  }

  const submit = (m: { id: string; address: string }, body: object) =>
    signed(
      h,
      'POST',
      `/internal/matches/${m.id}/result`,
      { mapVersion: 'hyd-city-v1', durationSeconds: 400, ...body },
      m.address,
    );

  it('keeps players in their place in the whole field and rewards by field size', async () => {
    const m = await match();
    const [a, b] = m.players;
    const r = await submit(m, {
      reason: 'WINNER',
      winnerAccountId: a.userId,
      bots: 4,
      participants: [
        { accountId: a.userId, placement: 1, kills: 4, survivalSeconds: 400 }, // more kills than the other player alone allows
        { accountId: b.userId, placement: 3, kills: 0, survivalSeconds: 250 },
      ],
    });
    expect(r.status).toBe(200);
    const stats = await h
      .http()
      .get('/api/v1/player/stats')
      .set(...bearer(b.accessToken));
    expect(stats.body.xp).toBe(100 + 10 * (6 - 3)); // 3rd of 6: outlasted three
  });

  it('accepts a bot as the winner, naming no one', async () => {
    const m = await match();
    const [a, b] = m.players;
    const r = await submit(m, {
      reason: 'WINNER',
      winnerAccountId: null,
      bots: 3,
      participants: [
        { accountId: a.userId, placement: 2, kills: 1, survivalSeconds: 390 },
        { accountId: b.userId, placement: 5, kills: 0, survivalSeconds: 100 },
      ],
    });
    expect(r.status).toBe(200);
  });

  it('refuses results that only make sense without bots, or name no winner while a player was first', async () => {
    const m = await match();
    const [a, b] = m.players;
    const noWinner = await submit(m, {
      reason: 'WINNER',
      winnerAccountId: null,
      bots: 2,
      participants: [
        { accountId: a.userId, placement: 1, kills: 0, survivalSeconds: 400 },
        { accountId: b.userId, placement: 2, kills: 0, survivalSeconds: 300 },
      ],
    });
    expect(noWinner.status).toBe(422);
    const outOfField = await submit(m, {
      reason: 'TIMEOUT',
      bots: 1,
      participants: [
        { accountId: a.userId, placement: 1, kills: 0, survivalSeconds: 400 },
        { accountId: b.userId, placement: 4, kills: 0, survivalSeconds: 300 }, // only 3 in the field
      ],
    });
    expect(outOfField.status).toBe(422);
    const tooManyKills = await submit(m, {
      reason: 'TIMEOUT',
      bots: 1,
      participants: [
        { accountId: a.userId, placement: 1, kills: 3, survivalSeconds: 400 }, // only 2 opponents
        { accountId: b.userId, placement: 2, kills: 0, survivalSeconds: 300 },
      ],
    });
    expect(tooManyKills.status).toBe(422);
  });
});
