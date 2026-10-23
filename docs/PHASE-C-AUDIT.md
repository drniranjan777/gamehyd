# Phase C: Battle Royale environment audit and build plan

Date: 23 October 2026. This is the audit asked for before the Phase C work (map, loot, inventory, vehicles, bots,
modes and the full match flow). Nothing in this document has been built yet. It records what the game already has
against the twelve tasks of the brief, what is missing, and the order to build it in.

## 1. Prerequisite: Phase B

The brief asks for Phase B to be complete first. Its code is complete and tested ([PHASE-B-STATUS.md](PHASE-B-STATUS.md),
parts 1 to 5): aiming, camera, weapon feel, the firing range, character animation plumbing, other players' aim and
reload, touch layout editor, haptics and rumble, and mobile performance work.

Phase B's acceptance test is **not** met, and code cannot meet it:

- **Characters are capsules** until the Mixamo character and animations are downloaded into
  `game/unity-client/Assets/ThirdParty/Mixamo/` (needs the client's Adobe account).
- **Not measured on a phone**: the 30 FPS budget, touch controls and vibration need an APK on a device.

Nothing in Phase C depends on the character art: the server and the simulation use the same capsule whatever is drawn.
**Recommendation:** build Phase C now, and close Phase B as soon as the Mixamo files and a phone are available.
Every Phase C test runs the Phase B systems (movement, camera, aim, weapons) again, so they are re-checked as we go.

## 2. What the game runs on

| Area                 | What is there                                                                                                                                                                                                  |
| -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Rules                | `RWBR.Shared` (netstandard 2.1): movement, two weapon slots, ammo, armor, medkits, ballistics, loot, safe zone, shared by the .NET match server and the Unity client                                           |
| Match server         | .NET 10, UDP, 30 Hz, authoritative, delta snapshots (protocol 6), prediction and reconciliation, lag compensation, reconnects, signed join tickets, results submitted to the platform                       |
| Map                  | `hyd-city-v1`: a fictional 600 m city in five districts (bazaar, towers, residential colony, lakeside, central gate) plus a metro viaduct. 3,871 boxes, 372 loot spawns, 32 spawn points. Flat ground |
| Bots                 | Shared `BotBrain` on a 2 m nav grid: heads into the zone, loots weapons, ammo, armor and medkits, fights what it can see. Used by the match simulator only. Practice mode uses an older, simpler `EnemyBrain` |
| Platform             | NestJS API and PostgreSQL: accounts, profiles, XP and seasons, Quick play matchmaking (Solo only), server registry, signed results, match history, store, admin portal                                       |
| Client flow          | Main menu → Quick play → queue → match → results → main menu, or offline practice and the firing range                                                                                                        |

## 3. Gap against the twelve tasks

| #   | Task                                     | Already there                                                                                                                       | Missing                                                                                                                                                                                                                         | Needs assets?                                                           |
| --- | ---------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------- |
| 1   | Hyderabad-inspired map                   | Bazaar (markets), colony (residential), towers, lakeside, enterable shops and houses with stairs to the roof, viaduct, roads        | **Warehouses and an industrial zone, a river with road bridges, open terrain** (the city is built edge to edge). Needs a new map version, `hyd-city-v2`, generated the same way and pinned by hash                              | No (box geometry). Final building art: yes                              |
| 2   | Terrain, collision, nav, cover, lighting, audio, rendering | Collision and nav grid shared with the server, cover boxes, chunked meshes with 2 LODs, synthesised gunshots and footsteps with occlusion | **Height terrain** (ground is flat; hills change movement and line of sight on the server too), **environmental audio** (traffic, market crowd, birds by the lake), **interior culling** for enterable buildings, a lighting pass | Ambient sound: placeholder in code, final needs a sound designer        |
| 3   | Minimap, compass, markers, spawns, loot locations | Spawn points and loot spawns per map                                                                                         | **Minimap and full map** (drawn from the same layout), **compass strip**, **markers** (your ping, zone, teammates), **configurable loot tables** (today loot is fixed in the generator)                                          | No                                                                      |
| 4   | Ground loot items                         | 6 weapons, ammo (one shared pool), armor vest, medkits                                                                              | **Helmets** (head damage reduction), **backpacks** (capacity), **ammo per calibre**, **attachments** (scope, extended magazine, grip, suppressor), **throwables** (frag, smoke), **more heals** (bandage, energy drink)          | No (simple shapes); final item art: yes                                 |
| 5   | Inventory                                | Pick up by walking over or pressing interact, 2 weapon slots, inventory panel                                                       | **Drop**, **stacking**, **capacity by backpack**, **equipment slots** (helmet, vest, backpack, attachments per weapon), **quick-use** (heal, throw). All server-authoritative                                                    | No                                                                      |
| 6   | Zone and victory                         | Shrinking zone with stages, damage, countdowns, eliminations, placements, winner, max match time                                    | **Match boundary** feedback, **zone damage rising per stage**, a **kill feed per team**, victory for a team (Duo/Squad)                                                                                                       | No                                                                      |
| 7   | Vehicles                                 | Nothing                                                                                                                             | The engine supports them. They have to live in the shared simulation, not Unity physics, so the server stays authoritative. Plan: a simple wheeled car (kinematic, not rigid-body), steering, throttle, brake, collision with boxes and players, fuel-free, enter and exit, driver and passenger seats, damage and destruction | No (box car). Final car model: yes                                      |
| 8   | Bots                                     | Navigating, looting, zone-aware `BotBrain` in the simulator                                                                         | **Bots in Unity matches** (offline bot match, and fill bots on the server so online matches are never empty), **weapon choice by range**, **difficulty levels** (reaction time, accuracy, aggression), **using the new items**      | No                                                                      |
| 9   | Modes                                    | Solo Quick play, Training (the firing range and tutorial), city practice with simple enemies                                         | **Duo and Squad** (teams in the server, snapshot and results; parties and team queues in the API and database), **Bot Practice** as a real match against `BotBrain` bots                                                       | No                                                                      |
| 10  | Full flow                                | Menu → queue → match → results → menu                                                                                               | A **lobby** screen (party, mode, ready), a **pre-match** area while the match fills, a **spawn mechanic** (drop or district spawn: still an open product decision from Phase 4), return to the lobby                            | No                                                                      |
| 11  | Mobile graphics                          | Quality presets, chunk LODs, character detail tiers, GPU skinning, budget telemetry, automatic quality                              | **Streaming** of map chunks by distance, **object pooling** for loot, tracers and effects, **interior culling**, LODs for items and vehicles                                                                                  | No                                                                      |
| 12   | Backend                                 | Profiles, results, XP, match history in PostgreSQL; nothing per frame is written                                                    | **Team results** and placements per team, **mode** on matches and history, **loot tables and bot difficulty** as versioned admin config, **party** tables                                                                     | No                                                                      |

## 4. Decisions needed

1. **Start Phase C before Phase B's assets arrive?** Recommended: yes (see section 1).
2. **Spawn mechanic.** A plane and parachute drop is the genre standard but adds a flight path, free fall and a landing
   to the simulation. Randomised spawns spread across districts are much simpler. The Phase 8 simulations show that
   spreading players out and starting unarmed is what matters for match length. Recommended: **district spawns with a
   loot-first start now**, and the drop later if wanted.
3. **Vehicles in the shared simulation.** Recommended: **yes, one car type**, simple and server-authoritative. Unity
   wheel colliders would run only on the client and could not be trusted online.
4. **Duo and Squad.** They touch the protocol, the server, the API, the database and the client. Recommended: build them
   after Solo is complete with all items, in part C6 below.

### Decisions (23 October 2026)

- Phase C starts now; Phase B closes when the Mixamo files and a phone are available.
- Spawn: **plane and parachute drop**, simulated on the server (flight path, jump, free fall, parachute, landing), with
  bots choosing where to jump. Built in C6. Until then, C1 to C5 use the existing spawn points.
- Vehicles: **one car** in the shared simulation (C4).
- Build order: C1 to C7 as below.

## 5. Build order

Each part is playable and tested on its own, in this order. Every part keeps the .NET, API and Unity suites green.

| Part | What                                                                                                                                                                                        |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| C1   | **Bot match offline**: a full Solo match in Unity against `BotBrain` bots on the city, with the zone, eliminations, a results screen and return to the menu. This is the brief's acceptance path, without vehicles or new items, early |
| C2   | **Items and inventory**: helmets, backpacks and capacity, calibres, attachments, throwables, heals, drop, stacking, equipment slots, quick-use, in shared rules, server and client (protocol 7) |
| C3   | **Map v2**: industrial estate with warehouses, a river with bridges, open terrain at the edges, height terrain if the server cost allows it, configurable loot tables, minimap, compass, markers, ambient audio |
| C4   | **Vehicles**: one car in the shared simulation, on the server and in Unity, with bots able to ignore or avoid them                                                                         |
| C5   | **Bots everywhere**: difficulty levels, weapon choice, using the new items, fill bots on the server for online matches                                                                    |
| C6   | **Modes and flow**: lobby, parties, Duo and Squad, Bot Practice, the plane and parachute drop, return to lobby; API and database changes for teams and modes                                             |
| C7   | **Mobile pass**: chunk streaming, pooling, interior culling, item and vehicle LODs; budget checked with the Phase B telemetry                                                              |

## 6. Acceptance test (from the brief)

"A player can enter a playable match, explore the map, collect equipment, fight bots, use implemented vehicles, survive
the safe zone, and complete a match with a results screen." C1 gives the first version of this. After C4 the whole
sentence is testable, offline and online, and a PlayMode test will drive it end to end. Characters will be capsules
until the Mixamo files are in. This report will say so rather than call it done.
