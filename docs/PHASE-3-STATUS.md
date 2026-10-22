# Phase 3 status — 22 October 2026

Phase 3 goal (PHASE-PLAN.md): durable accounts, progression and inventory, with a player's match result persisting. **The exit gate passes end to end on the dev machine.** It is not on a staging environment yet (needs a cloud account, ADR 0005), and platform sign-in waits on Google/Apple credentials.

## Exit gate

| Gate item                                              | Status       | Evidence                                                                                                                                                                                                                                                                      |
| ------------------------------------------------------ | ------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| A player plays a match and sees XP and history persist | **Done**     | `infrastructure/scripts/fullstack-smoke.sh`: fresh DB → API → managed match server → 4 bots through sign-up/matchmaking/tickets → XP, wins, coins, history, ledger checks all pass. Unity PlayMode `PlatformMatchTests`: the Unity client does the same and reads back its XP |
| Forged results rejected                                | **Done**     | Results naming players outside the match, from a non-hosting server, unsigned or wrongly signed, or conflicting with an accepted result are refused and recorded as evidence                                                                                                  |
| Integration tests                                      | **Done**     | 21 API tests on real PostgreSQL, every response validated against the OpenAPI contract; 10 database tests                                                                                                                                                                     |
| Staging environment                                    | **Not done** | Needs the cloud decision (ADR 0005)                                                                                                                                                                                                                                           |

## What was built

**Database (migrations 0002–0005; 18 tables of the 47-table design so far):** player profiles; rotating refresh-token sessions (hashes only); maps and immutable published map versions; game servers; matches with a database-enforced lifecycle; participants; matchmaking queue; append-only result evidence; wallets with an append-only ledger (version-checked, idempotency keys, drift view); cosmetics that cannot carry competitive stats; inventory and loadouts (you can only equip what you own); player reports; append-only audit log. Admin users and roles arrive with the portal (Phase 5).

**API contract:** `packages/api-contracts/openapi.yaml` is the source of truth. TypeScript types are generated from it (`pnpm --filter @rwbr/api-contracts generate`), and `lint` fails on drift.

**Platform API (NestJS):**

- **Auth:** guest accounts (device secret shown once, stored hashed), JWT access tokens (15 min), refresh tokens rotated on every use, replay of an old token revokes the whole sign-in and is audited, logout. Platform sign-in returns 501 until Google Play Games / Apple credentials exist.
- **Player:** profile (validated display names), stats, cursor-paginated match history.
- **Maps** (published versions and manifests), **inventory** (starter cosmetics, equip, wallets), **reports**.
- **Matchmaking on PostgreSQL** (`SKIP LOCKED`, no Redis needed yet): client-version gate, fills FORMING matches up to capacity, creates matches on idle live servers, hands out seats only after the server confirms it is hosting, reconnect tickets, results view.
- **Match-server endpoints (`/internal`, HMAC-signed with timestamp window):**
  - heartbeat: register, assign the next match, track hosting/active state, clean up after crashes;
  - result: hosting server only, validated against the roster, exactly-once rewards through the ledger, duplicates detected, conflicts refused, every submission recorded.
- Rate limits (per IP / per user; in memory until the API scales out), validation with per-field 422 details, request IDs, access logs without headers, bodies or tokens.

**Match server managed mode:** `--api-url`, with secrets taken from the environment. It heartbeats as idle, hosts the assigned match with tickets required (map, capacity, minimum players, fill-wait), reports its phase, then submits the signed result with retries. **Bots** can go through the platform (`--api`): sign up, matchmake, play on the seat, print their stats.

**Unity:** Online scene → **Quick play**: guest sign-in (credentials remembered), queue, wait for a seat, join with the signed ticket. The result screen shows level and XP gained, read back from the platform.

**Rewards:**

- XP: 100 for playing, +50 per elimination, +10 per player outlasted, +300 for the win.
- Coins: 10 for playing, +5 per elimination, +50 for the win.
- Level = XP / 1000 + 1.

These live in `apps/api/src/player/progression.ts` and need a game-design review.

## Tests

| Suite                                          | Result                                                                                                                                                                                                                                                             |
| ---------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Database (`pnpm --filter @rwbr/database test`) | 10/10: ledger append-only even for the owner, idempotent grants, map immutability, match lifecycle, ownership-enforced loadouts, no-stat cosmetics, audit log, credential/refresh-hash rules, role permissions                                                     |
| API (`pnpm --filter @rwbr/api test`)           | 21/21 on real PostgreSQL with contract validation: auth flows, refresh reuse detection, rate limits, validation, profile/stats/history, maps, inventory, reports, matchmaking loop, signatures, forged and conflicting results, exactly-once rewards, ledger drift |
| .NET                                           | 47/47: adds the cross-language ticket vector (Node-issued ticket verifies in C#)                                                                                                                                                                                   |
| Unity                                          | EditMode 15/15, PlayMode 5/5 (platform end to end included)                                                                                                                                                                                                        |
| Full-stack smoke                               | Passed: 4 players, 1 match, 4 rewards, 0 drift, 1 accepted / 0 rejected results                                                                                                                                                                                    |

## Not done in Phase 3 (and why)

- **Staging environment, secrets manager, metrics/tracing backend:** need the cloud decision (ADR 0005). Logs are already structured JSON.
- **Google Play Games / Apple sign-in:** need developer accounts and credentials. The endpoint and contract exist.
- **Secure credential storage on device:** guest credentials sit in PlayerPrefs for now; production must use the Keystore / Keychain.
- **Missions, leaderboards, friends/parties:** later phases per the plan.
- **Admin RBAC and MFA:** Phase 5 with the admin portal.
- **Redis:** not needed yet; matchmaking runs on PostgreSQL locks and rate limits are in memory. Revisit when the API runs as several instances.
- **Matches that never get hosted:** if an assigned server dies before hosting, queued players wait until that server heartbeats again. Needs a sweeper.
