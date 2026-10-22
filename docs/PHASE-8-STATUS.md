# Phase 8 status — 22 October 2026

PHASE-PLAN.md ends with Phase 7. "Phase 8" works through the **P1 engineering items of the [post-launch backlog](launch/POST-LAUNCH-BACKLOG.md)**: the ones that need code rather than accounts, cloud or people. The P0 launch blockers are unchanged.

## What was built

**Double-XP events.**

- `xpMultiplier` (1–5) is a live setting next to the kill switches: admin portal → Live ops, versioned and audited like every config change.
- Match XP, and therefore season XP, is multiplied when the server's result is recorded. Coins are not.
- Public `GET /config` shows the multiplier so the game and website can announce the event.

**Match-server draining for rolling deploys.**

- The first Ctrl+C or SIGTERM drains a managed server instead of killing it:
  - it reports `draining` in its heartbeats, and the API marks it DRAINING and gives it no new matches;
  - matches assigned to it but not yet hosted are cancelled at once, and their players go back to the queue;
  - a running match finishes and submits its result, then the process exits.
- A second signal stops it immediately. The rollback runbook now describes this procedure.

**Game client.**

- **News on the home screen:** current and upcoming live events from the platform, live ones marked. Nothing is shown when the platform is unreachable.
- **Clear platform messages:** "Matchmaking is paused for maintenance…", "A new version of the game is available…" and "This account can't play online right now…" replace raw error codes.
- **Report a player from the results screen:**
  - after a Quick play match, the game fetches the server-confirmed result (player ids come from the platform, not the client) and lists the other players;
  - you pick a reason and report; the report is tied to the match;
  - rate limits and duplicates are handled.

**Bots navigate and loot** (match simulator).

- A 2 m ground-level navigation grid for each map, with A* paths that don't cut corners and are smoothed by line-of-sight shortcuts. It's in `RWBR.Shared` (netstandard 2.1, its own heap).
- Bots:
  - head into the zone;
  - go for a weapon when unarmed and ammo when empty, otherwise armour or medkits nearby;
  - fight only enemies they can actually see (a wall ray test);
  - roam inside the zone;
  - jump and re-plan when stuck.
- The simulator has a `--loot-start` option (everyone starts unarmed).

## Simulation: match length on the city (risk 17)

30 bots, `hyd-city-v1`, production zone schedule, 3 matches each:

| Start                       | Average active play        | Player kills | Zone eliminations |
| --------------------------- | -------------------------- | ------------ | ----------------- |
| Phase 4 bots, armed         | ~2.2 min (4.6 / 1.1 / 0.9) | 28.7         | 0.3               |
| Navigating bots, armed      | **4.7 min**                | 26.7         | 2.3               |
| Navigating bots, loot-first | **5.9 min**                | 16.7         | 12.3              |

- **Navigation:** it alone more than doubles match length. The Phase 4 bots were stuck on buildings and fought at the spawns.
- **Loot-first:** adds about a minute but sends many bots to the zone unarmed (12 of 29 eliminations). On the current city, ground weapons are too sparse along the routes into the zone, or bots spend too long looting.
- **Still short of target:** both are well under the ~8 minute zone schedule. Bots are a proxy, not players.
- **Recommendation for the product decision:**
  - a drop/spawn spread across districts, plus a loot-first start with denser ground-floor weapons near spawns;
  - re-run `--simulate 10 --loot-start` after each loot or zone change.

## Tests

| Suite          | Result                                                                                                                                                |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| API            | double XP: XP ×2, season XP ×2, coins unchanged; draining heartbeat cancels unhosted matches, requeues players, marks the server DRAINING             |
| .NET           | 77/77 (was 73): drain against a stand-in platform; city paths valid from every spawn; ≥ 80% of ground weapons reachable; unarmed bots loot and finish |
| Unity EditMode | friendly platform errors; news and result DTOs                                                                                                        |
| Unity PlayMode | 8/8; the whole-platform test now also reports the opponent from the confirmed result                                                                  |

## Not done (and why)

- **Redis-backed rate limits and status cache:** needed only with more than 2 API instances; no Redis on this machine.
- **Cosmetics drawn on characters:** needs character art and a protocol field for other players' loadouts; waits for the art pipeline.
- **UDP bot clients still use the simple behaviour:** they're used for network soak tests, where the old movement is enough; the simulator uses the new bots.
- **All P0 items:** unchanged; see the backlog.
