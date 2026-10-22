# Phase 1 status — 22 October 2026

Phase 1 goal (PHASE-PLAN.md): a single-player game that feels good on a real phone. The code work is done and tested on the dev machine. **Phase 1 is not complete**: the exit gate needs measurements on a named mid-tier device, and the art pipeline needs an art lead.

## Exit gate

| Gate item                             | Status                               | Evidence                                                                                                                            |
| ------------------------------------- | ------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------- |
| Playable slice                        | **Done (desktop + touch code path)** | Practice mode in `Assets/Scenes/Greybox.unity`; 8 EditMode + 3 PlayMode tests                                                       |
| ≥ 30 FPS on the named mid-tier device | **Not measured**                     | No device here. `PerformanceMonitor` writes frame-time/memory JSON to `perf.jsonl` on the device every 10 s                         |
| Crash reporting live                  | **Not done**                         | Tool choice is open in ADR 0005                                                                                                     |
| Android build                         | **Done**                             | `unity-batch.sh android`: IL2CPP ARM64 development APK, 25.6 MB, built in ~9.5 min on the dev machine; not yet installed on a phone |

## What was built

**Shared rules (`RWBR.Shared`, used by Unity and the server):**

- Movement v2: gravity, jump (~1 m, ground only), crouch (slower, 1.2 m capsule, headroom check before standing), stairs and ledges up to 0.45 m, ceilings, falling off roofs.
- Greybox building now has outside stairs to a walkable roof.
- Combat: armor absorbs 50% of damage while it lasts; medkits heal 50 HP after 3 s and are interrupted by damage; reloads draw from reserve ammo; fire cadence; deterministic spread (standing / crouched / moving / airborne), identical on client and server.
- Loot: rifle, ammo, medkit and armor-vest spawns (12 in the greybox), pickup range 2 m, caps (5 medkits, 180 reserve rounds, 100 armor).

**Server (`RWBR.MatchServer`) on the same rules — protocol v2:**

- Server-checked pickups (range, each item once, no useless pickups), heal, reload, spread, crouch-aware hit capsules in lag compensation.
- Snapshots now carry armor, crouch/heal/weapon/grounded flags, a loot-taken bitset, and recipient-only reserve ammo and medkits.

**Unity vertical slice (practice mode):**

- Loop: spawn unarmed → enter the building → pick up a rifle → clear 5 AI enemies (roof, inside, market, apartments, cover) → victory or defeat → play again.
- Enemy AI: patrols, spots the player within 40 m with line of sight, reacts after 0.6 s, strafes and fires with its own weaker weapon balance.
- Third-person camera with wall pull-in; crosshair aim; recoil kick; tracers; hit and kill markers; damage flash; enemy health bars.
- HUD: health/armor, ammo and reload, heal progress, pick-up prompt, objective and kill feed, inventory (with "use medkit"), pause menu with sensitivity, Low/Medium/High quality and the performance overlay.
- Touch controls (ADR 0006), keyboard/mouse and gamepad.
- Quality presets: shadows, AA, LOD bias, texture mip limit, 30/30/60 FPS targets; the default depends on device RAM.
- `PerformanceMonitor`: avg/p95/p99/max frame time and memory every 10 s → `perf.jsonl` + log.

## Tests

| Suite                                                                                                                                  | Result                            |
| -------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------- |
| .NET (shared rules, server authority, UDP bots)                                                                                        | 33/33                             |
| Unity EditMode (map/collider match, practice simulation: pickup range, victory, enemies shoot back, no shooting through walls, medkit) | 8/8                               |
| Unity PlayMode (walk through the doorway, pick up rifle + fire, restart)                                                               | 3/3                               |
| Bots over UDP on the new rules                                                                                                         | 8 and 30 players: one winner each |

## Not done in Phase 1 (and why)

- **Device measurements** — need the signed-off device matrix and a phone; the APK and the perf log are ready for it.
- **Crash reporting** — waits on the tool decision (ADR 0005).
- **Art pipeline** (modular kit, materials, LODs, lighting, texture budgets) — needs an art lead and an art style guide. Greybox stays primitive.
- **Character animation and audio** — Phase 4 content per the plan; the slice uses capsules and no sound.
- **Unity CI builds** — need a Unity build licence secret.
- **Online play from Unity** — Phase 2: connect `PracticeGame`-style prediction to the match server.
