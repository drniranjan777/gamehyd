# Phase 4 status — 22 October 2026

Phase 4 goal (PHASE-PLAN.md): a content-complete alpha with one mode and one map. **The engineering side is done and tested. Content-complete is not reached.** Final art, character animation, licensed or original audio, and production UI need artists, a sound designer and an art director. Key design decisions (drop/spawn mechanic, loot-first start) are open.

## What was built

**The city: `hyd-city-v1`** (ADR 0008). A fictional 600 m map inspired by Hyderabad's street life, generated deterministically and shared byte-for-byte by the server and Unity:

| District                 | Content                                                                                 |
| ------------------------ | --------------------------------------------------------------------------------------- |
| Char Gate Plaza (centre) | Four-towered landmark gate with arches and a gallery overhead, fountain-wall cover      |
| Old City Bazaar (NW)     | Dense enterable shops in 2x2 blocks, narrow lanes, market stalls, some rooftop stairs   |
| Hi-Tech Towers (NE)      | 40–80 m office towers, parked cars as cover, enterable gatehouses                       |
| Residential Colony (SW)  | Houses with inside stairs to the roof, compound walls with gates                        |
| Lakeside (SE)            | Lake (impassable), tree-lined promenade (canopies block shots), kiosks with roof stairs |
| Metro viaduct (x = 150)  | Pillars at street level, a deck overhead                                                |

3,871 boxes, 372 loot spawns (weapons weighted by district), 32 spawn points. The geometry hash is pinned in a test and in migration 0006.

**Performance:**

- **Server:** a spatial grid keeps movement at 5 µs per step and line-of-sight at 4 µs per ray on the city.
- **Unity:** boxes merge into 64 m chunk meshes with a two-level LOD, using a custom vertex-colour shader.

**Weapon roster: balance v3**, six original weapons, server-controlled:

| Weapon         | Class         | Notes                                      |
| -------------- | ------------- | ------------------------------------------ |
| P9 Sidearm     | Pistol        | 12 rounds, accurate, weak                  |
| Kestrel SMG    | SMG           | Fastest close-range fire, heavy falloff    |
| Standard Rifle | Assault rifle | All-rounder                                |
| Pump-12        | Shotgun       | 8 pellets; owns 0–10 m, useless past ~25 m |
| Marksman DMR   | Marksman      | 3-shot body kill at any range              |
| Longshot       | Sniper        | 2-shot body, 1-shot head                   |

- **Mechanics:** two weapon slots, switch draw times, headshots (top 0.4 m of the body), damage falloff with distance, and deterministic shotgun pellets.
- **Quick-swap protection:** fire cooldowns are kept per slot, so switching weapons can't skip a sniper's bolt time.
- **One rule set:** the shared `Ballistics` code resolves shots for both the server and practice mode.
- **Database:** `weapons` and an append-only `weapon_balance_versions` table, plus a test that every value matches the catalog.

**Protocol v4:**

- a switch-weapon button;
- each player's weapon in hand and shot counter, so other players' shots appear as tracers and sound;
- the recipient's weapon slots;
- the balance version in `Welcome`.

**Audio (placeholder):**

- Sounds are synthesised in code: a gunshot per weapon class, footsteps (quieter when crouched), reload, pickup, swap, hit, headshot, kill and zone warning.
- Playback is 3D with distance falloff (up to 400 m for a sniper).
- Sounds are muffled through walls (using the game's line-of-sight test) and get room reverb indoors.
- Master, effects and interface volumes are separate.

**Front end and onboarding:**

- The main menu scene, with the city as backdrop, offers training ground, city practice, play online, profile and match history, loadout (equip owned cosmetics through the API) and settings.
- A 7-step first-run tutorial on the training ground (look, walk, enter, pick up, shoot, climb, done) runs with enemies holding fire until it's finished.
- City practice has 8 enemies spread across the districts.

**Balance tooling:**

- `docs/balance/TTK.md`: a time-to-kill table generated from the catalog (regenerated with `UPDATE_DOCS=1`, checked in tests). It caught a real issue: the shotgun didn't own close range. That was fixed before the numbers were committed.
- `RWBR.MatchServer --simulate N --map hyd-city-v1`: plays full matches in-process at about 200× real time.

## Simulation finding: match length on the city

| Run    | Active play | Player kills | Zone eliminations |
| ------ | ----------- | ------------ | ----------------- |
| seed 1 | 4.6 min     | 28           | 1                 |
| seed 2 | 1.1 min     | 29           | 0                 |
| seed 3 | 0.9 min     | 29           | 0                 |

The zone schedule for the city runs about 8 minutes, but matches end in 1–5. Everyone spawns armed along the two avenues (the prototype loadout), so fighting starts at once.

**Recommendation (needs a product decision, BRD Appendix A "drop/spawn mechanics"):**

- Start unarmed and loot first.
- Spread spawns across the map: a drop/parachute or randomised district spawns.

Re-run the simulator after the bots learn to navigate and loot (see below).

## Tests

| Suite          | Result                                                                                                                                                                                                                                                                                                                                                                                                          |
| -------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET           | 67/67, including: city pinned and sane; grid matches brute force; walk into a house and up its inside stairs to the roof; city performance budget; 8 bots finish a match on the city; catalog sanity; slots and switching; quick-swap protection; headshots; pellets; falloff; server weapon state in snapshots; database balance rows match the catalog; TTK doc current; no absurd TTKs; simulator accounting |
| Unity EditMode | 19/19: city and greybox rendering match the server (raycast box tops); audio clips audible and unclipped; footstep cadence                                                                                                                                                                                                                                                                                      |
| Unity PlayMode | 7/7: menu → city practice; full scripted tutorial; plus the earlier practice, online and whole-platform end-to-end tests                                                                                                                                                                                                                                                                                        |
| Database / API | 10/10 and 21/21 (migration 0006 applied)                                                                                                                                                                                                                                                                                                                                                                        |

## Not done in Phase 4 (and why)

- **Art:** modular building art, materials, textures, lighting, character models, weapon models, distant impostors. Needs an art lead and art direction. The kit and box kinds define what an art pipeline must export (ADR 0008).
- **Character animation:** capsules only; needs animators.
- **Final audio:** placeholders are synthesised; needs a sound designer and licensed or original assets.
- **Production UI:** the HUD and menus are still greybox IMGUI (ADR 0006).
- **Bot navigation and looting:** bots walk straight at targets and get stuck on buildings, so they can't yet validate loot balance or a loot-first start.
- **Drop/spawn mechanic, match length target, zone tuning sign-off:** product decisions; the simulator is ready to evaluate them.
- **Cosmetics on characters:** the loadout is stored and equipped through the API but not yet drawn on the capsules.
- **Device performance on the city:** needs the device matrix and phones.
