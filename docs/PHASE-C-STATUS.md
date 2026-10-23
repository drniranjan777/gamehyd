# Phase C status: Battle Royale environment

Plan and audit: [PHASE-C-AUDIT.md](PHASE-C-AUDIT.md). Phase B's code is complete. Its characters stay capsules until the
Mixamo files are in, and nothing in Phase C depends on them (decision of 23 October 2026).

## Part C1 (23 October 2026): offline bot match

A full Solo battle royale on the city against bots, offline, from the main menu:
**Main menu → "Bot match: Solo vs 19 bots (offline)" → countdown → match → results → Play again or Main menu.**

**One rule set offline and online.** The authoritative `Match` (and `PlayerState`) moved from the match server into
`RWBR.Shared` (`RWBR.Shared.Server`). The game now hosts the server's own match inside the client:

- `LocalMatch` runs the match tick by tick with `InProcessBot`s, the navigating and looting bots from Phase 8, as the
  other players. Bots play through real snapshot and input packets, exactly as on the server.
- The player's `NetClient` gets `ConnectLocal`: the same packets, passed in memory instead of UDP. Prediction,
  interpolation, the HUD, the kill feed and the results screen are the online ones, unchanged.
- So loot, the safe zone, damage, eliminations, placements and the result follow the same code as a server match.
  Every Phase C feature (items, vehicles, the drop, modes) will be built once in `Match` and work in both.
- The bot match starts unarmed (loot first), uses the city's production zone schedule (about 8 minutes to the final
  circle), and has a 5 second countdown.
- Pausing offline stops the match. A slow frame slows the match down by at most 4 ticks per frame rather than jumping.
- The results screen says "Offline bot match: not counted on your profile". Nothing is sent to the platform.
- The match simulator now uses the same `InProcessBot`, so the bot code is no longer duplicated.

### Changed files

- Moved: `game/dedicated-server/src/RWBR.MatchServer/{Match,PlayerState}.cs` → `game/dedicated-server/src/RWBR.Shared/Server/`
  (block namespace and C# 9 for Unity; one netstandard fix, `Array.Clear` with explicit bounds).
- New: `RWBR.Shared/Server/LocalMatch.cs` (`LocalMatch`, `InProcessBot`).
- Server and tests: `using RWBR.Shared.Server` added; `MatchSimulator` uses `InProcessBot`.
- Unity: `NetClient.cs` (`ConnectLocal`, `IsLocal`), `OnlineGame.cs` (`StartBotMatch`, `RestartBotMatch`, ticking the
  local match, no silence reconnect offline), `OnlineHud.cs` (Play again, offline label), `MainMenu.cs` (Bot match button).

### Tests

| Suite          | Result                                                                                                                                                                                                                                                                         |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| .NET           | 84/84. New: a 12-player bot match on the city, hosted in-process, runs to a result with the local player placed, bots looting and fighting; pausing stops ticks and a 5 s frame runs only 4 ticks                                                                       |
| Unity EditMode | 57/57                                                                                                                                                                                                                                                                          |
| Unity PlayMode | 18 passed, 2 skipped (the two platform tests that need `TEST_ADMIN_DATABASE_URL`). New, end to end in the game: load the bot match, join, countdown, pause stops the match, walk 1 s and the authoritative match moves the player, play to the result, back to the main menu |

### Performance

- Simulator, 20 players on the city, loot-first, 2 matches: 154× and 282× real time on the dev laptop (Core i5
  VivoBook), so hosting the match and 19 bots costs well under 1 % of one core per tick here. A mid-range phone is
  several times slower, which should still leave it in the low single digits. Not yet measured on a phone.
- Match length with 20 players and a loot-first start: 6.1 and 4.9 minutes of active play. About 40 % of
  eliminations were the zone: bots still loot too slowly or too far from the circle. C5 (bots) and C3 (loot tables)
  address this.

## Part C2 (23 October 2026): items and inventory

Everything here is server-authoritative and lives in `RWBR.Shared`. The dedicated server and offline bot matches run
the same code, and practice uses the same inventory rules.

**Items** (item balance version 1, `Items.cs`):

| Item                                 | What it does                                                                                                                  |
| ------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------- |
| Ammo: light, medium, shells, heavy   | Light for the pistol and SMG, medium for the rifle and DMR, shells for the shotgun, heavy for the sniper. Each weapon reloads only from its own |
| Bandage                              | +15 health, only up to 75, in 2.5 s                                                                                           |
| Medkit                               | +75 health in 5 s (was +50 in 3 s)                                                                                            |
| Energy drink                         | 2 s to drink, then +25 health a little at a time over 10 s                                                                    |
| Vest, level 1 to 3                   | 40, 70 or 100 points; absorbs half of body damage while it has points                                                         |
| Helmet, level 1 to 3                 | 40, 70 or 100 points; absorbs half of head damage while it has points, then breaks                                            |
| Backpack, level 1 to 3               | Carrying capacity 200, 250 or 300 (100 without one)                                                                          |
| 4x scope                             | Rifle, SMG, DMR: aims at 4x zoom (or more) with 20 % tighter aimed spread                                                    |
| Extended magazine                    | Everything but shotgun and sniper: 50 % more rounds                                                                          |
| Grip                                 | Rifle, SMG, DMR: 25 % less moving spread, 30 % less recoil                                                                   |
| Frag grenade                         | 3.5 s fuse, bounces off walls and floors, up to 110 damage falling to 0 at 6 m, blocked by walls between                      |
| Smoke grenade                        | A 5.5 m cloud after 1.5 s that lasts 14 s; you can't see through it, and bots can't either                                   |

**Inventory:**

- **Weight:** ammo, heals, grenades and spare attachments sit in the backpack and have weight. A pickup takes as much
  as fits, and the rest stays on the ground.
- **Swapping:** a better vest, helmet or backpack replaces the one worn, and the old one is dropped. A third weapon
  replaces the one in hand, which is dropped with its magazine and attachments.
- **Attachments:** they go straight onto a weapon that takes them, else into the backpack. They move onto a new
  weapon automatically.
- **Spare magazine:** a weapon from a map loot spot comes with a full magazine and one spare.
- **Dropping:** you can drop part of a stack or all of it, your equipment, either weapon, or an attachment. A
  backpack can't be dropped while the rest wouldn't fit without it.
- **Elimination:** an eliminated player's whole inventory is left on the ground as separate piles.

**Loot table** (`LootTable`, version 1): the published map's loot spots are unchanged (still pinned by hash).
Which item each spot holds is decided per spot, deterministically, so server and clients agree without sending it.
On the city:

- **Ammo spots:** one of the four calibres (45 light or medium, 15 shells or heavy). About 10 % are frags, 5 %
  smokes and 15 % attachments instead.
- **Medkit spots:** bandages ×3 (50 %), a medkit (30 %) or an energy drink (20 %).
- **Armour spots:** a vest, helmet or backpack, at level 2–3 where the map placed strong armour and level 1–2
  elsewhere.
- **Small maps:** the training ground and the range keep simple loot (rifle ammo, medkits, vests).

**Protocol 7:**

- **Inputs:** an item and a target are now sent, for Throw, Use item, Drop item and picking up a particular item.
  Interact with no target still takes the nearest useful item.
- **Snapshots, your own state:** your backpack contents, vest, helmet and backpack levels, and attachments per slot.
- **Snapshots, nearby items:** up to 24 ground items within 40 m, sent only when they differ from the baseline (one
  byte otherwise).
- **Snapshots, grenades:** up to 12 grenades and smoke clouds within 150 m.
- **Old clients:** older clients are refused with the existing version check.

**Bots:**

- **Looting:** they pick up ammo for their own weapon and only armour or backpacks better than what they wear. They
  also loot dropped items and what eliminated players leave. Grenades and attachments are only worth a 10 m detour.
- **Healing:** they heal when nobody is in sight.
- **Smoke:** they can't see through it.
- **Not yet:** they don't throw grenades (C5).

**In the game** (offline bot match and online):

- **Bag screen:** Tab or the BAG button opens it. It shows equipment with Drop, both weapons with their attachments
  and Drop, and backpack contents with Use, Drop half and Drop all. It also lists everything within reach, including
  what an eliminated player left, each with a Take button.
- **Quick bar:** tap a heal to use it, or tap a grenade to select it. On touch screens it is a column on the right
  edge, clear of the buttons.
- **Throwing:** G or the new THROW touch button throws the selected grenade. T or d-pad right switches between frag
  and smoke. The touch button shows which grenade is selected.
- **Pick-up prompt:** it names the nearest useful item and how many more are in reach. It uses the same rules as the
  server, from a copy of your inventory rebuilt from each snapshot (`Combatant.FromSnapshot`).
- **On screen:** dropped items float like map loot, coloured by type. Grenades fly, frags flash with a blast sound,
  and smoke is a grey cloud.
- **HUD:** shows vest, helmet and backpack levels, weight and capacity, the calibre's reserve, and fitted attachments.
- **Practice:** shows the new heals and gear. It has no grenades (practice doesn't simulate them), so grenade loot
  is hidden there rather than shown and useless.

**Other fixes:**

- **City map cache:** the city map is now built behind a thread-safe `Lazy`. Two threads could each build their own
  copy before, which made a .NET test flaky.
- **Release binaries:** the end-to-end online test needs fresh ones (`dotnet build -c Release`) after a protocol
  change.

### Changed files (C2)

- Shared, new: `Items.cs` (catalog, `ItemStack`), `Server/MatchItems.cs` (ground items, pickups, drops, grenades).
- Shared, changed: `Combatant.cs` (inventory), `Loot.cs` (`LootTable`), `MapLayout.cs`, `GameRules.cs`,
  `InputCommand.cs`, `Protocol/Packets.cs`, `Protocol/SnapshotCodec.cs`, `Server/Match.cs`, `Server/PlayerState.cs`,
  `Bots/BotBrain.cs`, `CityMap.cs`.
- Unity: `OnlineGame.cs`, `OnlineHud.cs`, `OnlineViews.cs`, `PlayerInput.cs`, `TouchControls.cs`, `GameAudio.cs`,
  `PracticeSimulation.cs`, `PracticeGame.cs`, `PracticeHud.cs`, `PracticeViews.cs`.

### Tests (C2)

| Suite          | Result                                                                                                                                                                                                                                                                                                                                     |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET           | 97/97. New: weight limits and bigger backpacks; calibres per weapon; attachments fit and change balance, magazines return rounds; helmet and vest absorption, upgrades drop the old one; the three heals; drops and elimination piles; the city table holds every item and is deterministic; drop then pick up by id, partial pickups leave the rest; eliminated players' items in snapshots; ground items resent only on change; a frag bounces off a wall, hurts in the open and not behind cover; smoke blocks lines of sight and clears; heal buttons |
| Unity EditMode | 60/60. New: the inventory mirror from a snapshot (scope zoom, calibre reserve, weight, what can be taken); practice hides grenades; the THROW button overlaps nothing at 16:9 and 19.5:9                                                                                                                                                     |
| Unity PlayMode | 19 passed, 2 skipped (platform tests without `TEST_ADMIN_DATABASE_URL`). New, in an offline match: the inventory arrives, a dropped bandage lies on the ground and is taken back by id, the bag screen draws, a frag explodes, the next throw uses the smoke and makes a cloud                                                                |

### Match balance with items (simulator, 20 bots, city, loot-first, 3 matches)

| Build                         | Active play | Player kills | Zone eliminations |
| ----------------------------- | ----------- | ------------ | ----------------- |
| C1 (no items)                 | 5.5 min     | 11.5         | 7.5               |
| C2 first try                  | 7.6 min     | 5.7          | 13.7              |
| C2 (spare magazine, more ammo) | 6.2 min     | 12.3         | 6.7               |

The first try split ammo across four calibres. Bots ran dry and wandered away looking for rounds until the zone
caught them. A spare magazine with each map weapon, larger ammo piles and short detours for nice-to-have items
brought kills back above C1.

### Before players get this build

The production server runs protocol 4. This client is protocol 7. The match server built from this commit has to be
deployed with it (plus migration 0010 from Phase B) before an APK can join live matches. Offline bot matches work
either way.

## Part C3 (23 October 2026): map v2, moving zone, loot tables, minimap, compass, markers, ambience

**`hyd-city-v2`, a 900 m map.** v1 is untouched at the centre. It comes from the same generator, called first, so v1's
pinned hash is unchanged. A 150 m ring is added around it:

| District          | Content                                                                                                                       |
| ----------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| Industrial Estate (north) | Enterable warehouses (7 m walls, wide doors on two sides, racks inside as cover, loot between them), container yards stacked one to three high, a factory with a 45 m chimney |
| Musi Riverside (east)     | A river along x = 340–368 crossed by three road bridges (deck and railings) at z = −220, 0, 220, ghat walls along the city bank, farmland and farmhouses on the far bank |
| Outskirts (south)         | Open fields with rocks, lone trees and haystacks for cover, four farmhouses with roof stairs, loot out in the open (snipers and DMRs favoured) |
| Logistics Park (west)     | Rows of container stacks, two warehouses, a truck depot                                                               |

- **Size:** 4,522 boxes (651 new), 443 loot spots, 40 spawns. Pinned by hash in a .NET test and in migration
  `0011_city_map_v2.sql`.
- **The rest of the map:** the metro viaduct and both avenues run on to the map edge. The ring has concrete and
  field ground colours (visual only; the ground stays flat).
- **Water:** water now blocks movement from any height and can never be stood on. A test found you could jump from a
  riverside wall onto the 1.3 m water barrier and walk across the river; the lake in v1 had the same weakness.
  Shots still pass over water.
- **Where v2 is used:** the offline bot match plays on v2. Online matchmaking stays on v1
  (`DEFAULT_MAP_VERSION=hyd-city-v1`) until match servers that know v2 are deployed. Switch it to `hyd-city-v2`
  then. Servers now advertise v2.

**Moving zone (protocol 8):**

- **Moving circles:** with `ZoneSeed` set, each circle closes on a random point inside the previous one. Every
  later circle lies inside the one before, and the whole circle stays on the map. Seed 0 keeps the old centred zone.
- **Seeds:** the server seeds the zone from the match id; offline matches use a random seed.
- **Rising damage:** each later stage does 50 % more zone damage (`ZoneDamageGrowth`).
- **Snapshots:** they carry the next circle (`NextZoneX/Z/Radius`).
- **Bots:** they head for the next circle as soon as it is announced. If a path to their goal fails, for example
  when the circle's centre is on the lake or too far for one search, they aim part of the way there instead of
  standing still.

**Configurable loot tables:**

- **Settings:** `LootTableConfig` holds 16 weights: grenade, attachment and calibre shares, rounds per pile, heal
  and armour shares, and the chance of a higher level.
- **Admin portal:** Live operations → Loot table edits them. They are versioned and audited like every other config
  change.
- **API:** validates them (zod, 422 when out of range) and sends them with each match assignment.
- **Server:** clamps them rather than rejecting them, so a bad edit can't stop matches.
- **Clients:** get them in Welcome and derive the same items for every spot from them.
- **Contract:** the OpenAPI contract has a `LootTable` schema, and the types were regenerated.

**Minimap, compass, full map, markers** (`MapHud`):

- **Map picture:** a top-down image built once per map version from the same `MapLayout` as the server: ground,
  roads, buildings shaded by height, water, trees and containers.
- **Minimap:** top right, north-up, about 150 m around you. It shows the zone (blue), the next circle (white), your
  heading and your marker, with the district name under it. On touch screens it sits left of the pause button.
- **Compass:** along the top. It shows bearings, the heading in degrees, your marker with its distance and, when
  you're outside the next circle, the way to it with the distance.
- **Full map:** M, d-pad left or the new MAP touch button. It shows district names and both circles. Tap to place a
  marker and tap it again to clear it. The marker also shows as a tall yellow beacon in the world.
- **Practice:** the MAP and THROW buttons are hidden where they would do nothing.

**Ambience:** each district kind has a quiet, synthesised 6 s loop that crossfades as you move:

- **Bazaar:** murmur.
- **Industrial Estate and Logistics Park:** machinery hum.
- **Lakeside and Musi Riverside:** lapping water.
- **Outskirts and Residential Colony:** breeze and birds.
- **Everywhere else, including the avenues:** traffic.

These are placeholders until recorded ambience exists.

**Not built in C3:**

- **Height terrain:** the ground is still flat everywhere. Hills would change movement and line of sight on the
  server, as well as the nav grid, and a box-only simulation can't express slopes. This needs a terrain heightfield
  in the shared simulation, a larger piece of work. Recorded as a dependency, not faked with stepped boxes.
- **Interior culling for enterable buildings:** part of the mobile pass, C7.

### Changed files (C3)

- Shared: `CityMap.cs` (v2 generator), `MapLayout.cs` (box kinds, v2 lookup), `Simulation.cs` (water rule),
  `Loot.cs` (`LootTableConfig`, `LootTable.Apply`), `MatchConfig.cs`, `Protocol/Packets.cs`,
  `Protocol/SnapshotCodec.cs`, `Server/Match.cs` (zone, loot), `Server/MatchItems.cs`, `Server/LocalMatch.cs`,
  `Bots/BotBrain.cs`.
- Server: `Platform/ManagedServer.cs`, `Platform/PlatformClient.cs`, `Program.cs`.
- Platform: `apps/api/src/store/game-config.service.ts`, `apps/api/src/matchmaking/matchmaking.controller.ts`,
  `apps/admin/app/(portal)/liveops/page.tsx`, `apps/admin/lib/actions.ts`, `apps/admin/app/globals.css`,
  `packages/api-contracts/openapi.yaml` (+ generated `schema.ts`), `database/migrations/0011_city_map_v2.sql`.
- Unity: `MapHud.cs` (new), `OnlineGame.cs`, `OnlineHud.cs`, `OnlineViews.cs`, `GreyboxWorld.cs`, `GameAudio.cs`,
  `PlayerInput.cs`, `TouchControls.cs`, `PracticeHud.cs`.

### Tests (C3)

| Suite          | Result                                                                                                                                                                                                                                                                             |
| -------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET           | 109/109. New: v2 pinned; v1 intact inside v2 and all nine districts; spawns clear and no buried loot; the river crossed only on bridges and walking (and jumping) into it stops at the bank; 67/67 ring ground loot reachable from the centre; each circle inside the last and on the map for four seeds; snapshots carry the next circle and seed 0 stays centred; zone damage grows per stage; the loot table in Welcome gives the client the server's items; clamping; assignments parse the table |
| API            | 127/127. New: a published loot table keeps its values and defaults the rest, and out-of-range values are refused (422); assignments carry the loot table                                                                                                                             |
| Database       | 10/10 with migration 0011                                                                                                                                                                                                                                                         |
| Unity EditMode | 63/63. New: v2 renders box for box with colliders on the server's box tops; the map picture is built once per version; every district has a soundscape and each loop is audible and unclipped                                                                                    |
| Unity PlayMode | 22/22, including the two full-platform tests (API, database, a managed match server and the game, run with the dev database this time). New: an offline match on v2 has the match's loot table, an off-centre next circle, a marker that places and clears, the map screen, and the district's ambience |

### Performance and balance (simulator, 20 bots, loot-first, 6 matches each)

| Map and zone                         | Active play | Player kills | Zone eliminations |
| ------------------------------------ | ----------- | ------------ | ----------------- |
| v1, centred zone (old behaviour)     | 7.6 min     | 9.3          | 10.0              |
| v1, moving zone, damage rising       | 7.4 min     | 8.0          | 11.5              |
| v2 (3 matches), moving zone          | 7.3 min     | 3.3          | 15.7              |

- **Last part's figure:** the C2 number (12.3 kills, 6.7 zone eliminations) came from 3 matches. Over 6 matches the
  old-style zone gives about 9 kills and 10 zone eliminations, so the 3-match sample was partly luck.
- **Moving zone:** it costs about 1.5 more zone eliminations per match, which is expected: a moving circle catches
  stragglers.
- **v2 is too big for 20 bots:** players spread over a much larger area meet rarely, and most bots die in the zone.
  The bots' loot-and-move logic, not the map, is the limit. The plane drop (C6) and better bots (C5) are the planned
  fixes. Until then v2 suits larger lobbies.
- **Server cost:** the match runs at 150–190× real time on the dev laptop on both maps.

## Part C4 (23 October 2026): vehicles

One car type in the shared simulation, so the server decides everything and offline matches drive the same way
(`Vehicles.cs`, `Server/MatchVehicles.cs`).

**Driving** (`VehicleSim`, a kinematic "bicycle" model rather than a rigid body):

- **Speed:** accelerates at 6 m/s² to 22 m/s (about 80 km/h). Pulling back brakes hard (14 m/s²), then reverses
  slowly (up to 6 m/s). It rolls to a stop when you let go.
- **Steering:** 30° of lock at a standstill, down to about 7° at top speed, so it turns tightly when slow and gently
  when fast.
- **Handbrake:** Jump or space stops it within about a second.
- **Collisions:** the car is two circles along its length. Kerbs and bridge decks up to 0.3 m are driven over.
  Anything taller stops it: it bounces back and takes damage of 6 health per m/s of speed lost. Water always stops
  it, so the river is crossed by car only on the bridges. The car drives on the ground only.
- **Health:** 600.

**In a match:**

- **Spawns:** cars are parked along the avenues: 8 on v1, plus 4 in v2's ring by the warehouses, fields, logistics
  park and across the river. A test checks that every one is clear, on the road, and away from player spawns. The
  small maps have none.
- **Getting in and out:** F, d-pad down, or the GET IN / GET OUT touch button (shown only near a car with a free seat,
  or inside one). It takes the nearest car within 3.2 m, driver's seat first. Getting out puts you beside your door,
  or the other side if that's blocked. Jumping out faster than 6 m/s hurts, 4 health per m/s over.
- **Seats:** two. The driver steers and can heal but can't use weapons. The passenger can shoot, reload, switch and
  heal, but not pick up, drop or throw. Occupants sit crouched in their seats and can be shot.
- **Hitting people:** someone hit at 4 m/s or more takes 4.5 health per m/s (a full-speed hit does about 99), and the
  kill counts for the driver. Slower contact just pushes them aside, and parked cars push people out of their
  footprint.
- **Damage and wrecks:** frags damage cars at twice the damage they would do to a person at that distance. At zero
  health the car is a wreck: everyone inside is thrown out and takes 40 damage, so does anyone within 4 m, and a wreck
  can't be driven. An eliminated occupant leaves the car first, so their loot lands beside it.

**Protocol 9:** every car is in the snapshot, delta-encoded like players. A car is written only when its position,
heading, speed, health or occupants changed since the baseline. Parked cars cost nothing; a moving car costs about
12–17 bytes.

**In the game:**

- **Look:** a placeholder box body, cabin and four wheels that turn with speed, in five paints. Wrecks go dark.
- **Movement on screen:** cars are interpolated with the same delay as other players. You are drawn in your seat in
  the interpolated car, so the camera follows smoothly.
- **Engine sound:** a synthesised loop per car whose pitch and volume rise with speed, silent when parked and empty.
- **Controls:** driving, the stick is the car's own controls (W/S or up/down for throttle and brake, A/D for steering,
  not camera-relative) and space is the handbrake. The mouse still looks around.
- **HUD:** shows "Driving" or "Passenger", the speed in km/h and the car's health. The prompt shows
  "[F] Get in the car (drive / ride as passenger)".
- **Keys:** pick-up moved to E only, and F is now the car key.
- **Practice:** the GET IN button is hidden there.

**Not built in C4, and why:**

- **Your own car isn't predicted on your device.** The server moves it and you see it after the network delay.
  Offline that's one frame. Online, at 50–100 ms round trip, steering will feel a little late. Client-side prediction
  of the car is the next step for online driving feel.
- **Bullets don't damage cars.** Only grenades, crashes and wrecks do. The shared ballistics only test player
  capsules. People inside can be shot.
- **Bots don't drive.** They walk past parked cars (they're pushed aside like players), and a bot can be run over.
  Driving bots belong to C5.
- **One car model, placeholder look.** Final art needs a licensed or original car model.

### Changed files (C4)

- Shared, new: `Vehicles.cs` (`VehicleState`, `VehicleSim`, `VehicleSpawns`), `Server/MatchVehicles.cs`.
- Shared, changed: `InputCommand.cs` (Vehicle button), `MatchConfig.cs` (`Vehicles`), `Protocol/Packets.cs`
  (`VehicleEntry`, protocol 9), `Protocol/SnapshotCodec.cs` (car deltas), `Server/Match.cs` (seats, in-car rules,
  stepping), `Server/MatchItems.cs` (frags damage cars), `Server/PlayerState.cs`.
- Unity: `OnlineGame.cs` (car state, driving controls, seat rendering), `OnlineViews.cs` (car views, engine),
  `OnlineHud.cs` (car HUD, prompt), `RemoteInterpolator.cs` (car sampling), `ClientPrediction.cs` (paused in a car),
  `PlayerInput.cs`, `TouchControls.cs`, `GameAudio.cs` (engine loop).

### Tests (C4)

| Suite          | Result                                                                                                                                                                                                                                                                                    |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET           | 119/119. New: acceleration, top speed, braking then reversing, rolling to a stop; tighter turns when slow, handbrake; a wall stops and damages the car; kerbs driven over, walls not; the river crossed only on the bridge; every car spawn clear and away from player spawns; two players get in, the driver can't shoot and the passenger can, both ride along, getting out fast hurts and lands beside the car, it rolls to a stop; a run-over kill counts for the driver and a slow nudge only pushes; a frag wrecks a car and throws its occupant out, and a wreck can't be entered; parked cars cost nothing in delta snapshots and a moving one costs under 20 bytes |
| Unity EditMode | 64/64. New: the engine loop is audible, unclipped and seamless                                                                                                                                                                                                                             |
| Unity PlayMode | 23/23, including both full-platform tests. New, in an offline match: walk to a car, F to get in, the stick drives it more than 6 m in 2 s, every car is drawn, you are drawn in your seat, F to get out beside it                                                                         |

**Bots with cars on the map** (city, 20 bots, 6 matches): 7.4 min, 8.2 kills, 11.0 zone eliminations, essentially the
same as without cars (8.0 / 11.5), so parked cars don't trip the bots up. The simulator runs at 122× real time with
eight cars.

## Part C5 (23 October 2026): bots everywhere

**Skill levels** (`BotSkill`): Easy, Normal and Hard.

| | Reaction | Aim error | Engage range | Grenades | Picks weapons by range | Heals below |
| --- | --- | --- | --- | --- | --- | --- |
| Easy | 0.9 s | ±4° | 30 m | no | no | 50 |
| Normal | 0.45 s | ±1.7° | 45 m | yes | yes | 70 |
| Hard | 0.2 s | ±0.7° | 70 m | yes | yes | 80 |

**New behaviour** (`BotBrain`):

- **Reaction time:** a newly seen enemy is shot at only after the bot's reaction time.
- **Range:** the engage range is also capped at 80 % of the weapon's own range.
- **Weapon choice:** the bot swaps to the other slot when that weapon clearly suits the range better (shotgun and SMG
  close, rifle in the middle, DMR and sniper far), or when the one in hand is out of ammo. At most once a second.
- **At range, like players do:** beyond 20 m bots aim down sights. Beyond 35 m, Normal and Hard bots stop strafing and
  crouch. Before this, Hard bots fired at 70 m while strafing and hit only 28 % of their shots.
- **Frags:** when an enemy seen in the last 3 s ducks out of sight 7–22 m away, the bot lobs a frag where they were.
  The throw pitch comes from a table built with the server's own grenade physics, so it lands on target. A test throws
  real server grenades at 8, 14 and 20 m.
- **Smoke:** hurt (under 40) and under fire within 40 m, the bot throws smoke between them, then retreats to heal as
  before. One grenade every 6 s at most.

**Fill bots on the server:**

- **Topping up:** `MatchConfig.FillBotsTo` tops a match up to that many players with bots, at
  `MatchConfig.BotDifficulty`, when the countdown starts.
- **Inside the match:** the bots live in `Match`. They join without a ticket, even on ticket-only servers, through
  an internal path, not a Hello packet. They send inputs and receive snapshots through the same packet code, handled
  in memory and removed from the outbox, so the network host never sends them anything.
- **Live ops:** the admin portal (Live operations) gets "Fill empty seats with bots" and the bot difficulty. These are
  versioned and audited, travel in each match assignment, and are **off by default**.
- **Results:** the server lists players only, plus `bots: N`. Each player keeps their place in the whole field (5th
  behind four bots is 5th, not 1st). Kills on bots count, and a bot can win: a WINNER result with no winner named.
- **API:** validates placements within the whole field and the kill cap against it. Rewards scale with the whole
  field (XP for players outlasted includes bots).

**Offline bot match:** the main menu has a difficulty button next to "Bot match", which cycles Easy, Normal and Hard
and is remembered.

**Simulator** (city, 20 bots, loot-first, 6 matches each; `--difficulty easy|normal|hard`):

| Bots   | Active play | Shots | Hit rate | Player kills | Zone eliminations |
| ------ | ----------- | ----- | -------- | ------------ | ----------------- |
| Easy   | 7.4 min     | 64    | 46 %     | 6.3          | 12.8              |
| Normal | 7.4 min     | 86    | 46 %     | 8.0          | 11.2              |
| Hard   | 7.2 min     | 85    | 58 %     | 9.5          | 9.5               |

**Not built in C5:**

- **Driving:** bots don't drive. A useful driving bot needs road-following and getting out at fights, a project of
  its own. Cars are still there for players.
- **Squads:** bots don't revive or team up. Teams come with Duo and Squad in C6.
- **Large lobbies:** fill bots make every online match full, but 20+ bots on the 900 m map still mostly meet the zone
  rather than each other. The plane drop (C6) spreads everyone across the map and is the next lever.

### Changed files (C5)

- Shared: `Bots/BotSkill.cs` (new), `Bots/BotBrain.cs`, `MatchConfig.cs`, `Server/Match.cs` (fill bots),
  `Server/PlayerState.cs` (`IsBot`), `Server/LocalMatch.cs` (difficulty, `InProcessBot.Attach`).
- Server: `Platform/ManagedServer.cs` (fill bots from the assignment, results with bots),
  `Platform/PlatformClient.cs`, `Program.cs` (`--difficulty`), `Simulation/MatchSimulator.cs`.
- Platform: `apps/api/src/matchmaking/{matchmaking.controller,results.service}.ts`,
  `apps/api/src/store/game-config.service.ts`, admin live-ops page and action, OpenAPI contract and generated types.
- Unity: `MainMenu.cs`, `OnlineGame.cs` (remembered difficulty).

### Tests (C5)

| Suite          | Result                                                                                                                                                                                                                                        |
| -------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET           | 127/127. New: harder bots react sooner and reach further; bots switch to the shotgun up close and the sniper far away, not when the rifle is fine, and easy bots don't switch; a frag lobbed at an enemy who ducked out of sight, then not again straight away, and never by easy bots; throw pitches land server grenades near 8, 14 and 20 m; offline matches use the chosen difficulty; on a ticket-only server five fill bots join one ticketed player, fight, never put traffic on the network, and the result lists the player at their real place with `bots: 5` |
| API            | 130/130. New: a player 3rd behind bots keeps 3rd and is rewarded for a field of six; a bot winner names no one; results naming no winner while a player was 1st, placing outside the field, or with more kills than opponents are refused; assignments carry the bot settings (off by default) |
| Unity EditMode | 65/65. New: the bot difficulty is remembered, and garbage is clamped                                                                                                                                                                          |
| Unity PlayMode | 23/23, including both full-platform tests                                                                                                                                                                                                     |

## Part C6a (23 October 2026): teams (Duo and Squad)

C6 is built in three slices: **C6a teams**, C6b the plane and parachute drop, C6c parties, team queues and the lobby.

**Teams in the shared match:**

- **Team size:** `MatchConfig.TeamSize` is 1 for Solo, 2 for Duo, 4 for Squad. Solo is exactly as before: every
  player is a team of one, and all the earlier tests pass unchanged.
- **Who joins which team:** players join teams in order, filling the incomplete team first. A lone player in Duo or
  Squad bot practice, or with fill bots online, gets bot teammates. `MatchConfig.Teams` (account → team) lets the
  platform keep a party together (used in C6c). Those teams are reserved and never filled with strangers.
- **Spawning:** teammates start side by side at their team's spawn point.
- **No friendly fire:** shots pass through teammates (they aren't even targets), frags spare the thrower's team (the
  thrower can still hurt themselves), and a car never hurts its driver's teammates.
- **Placements:** a team places when its last member is out, and every member shares that placement. The kill
  feed's placement is 0 for a player whose team is still in.
- **Winning:** the match ends when one team is left, and its members share 1st. At the time limit, teams still
  standing are ranked by their combined health.
- **Protocol 10:** Welcome carries the team size, and each snapshot lists the recipient's teammates.

**Bots:** they never aim at teammates, and when roaming they stay within about 25 m of a living teammate who is safely
in the zone.

**In the game:**

- **Teammates:** drawn in green, shown as green dots on the minimap, and listed down the left with health bars (or
  "out").
- **Results:** the results screen says "YOUR TEAM WINS" or "Team #3".
- **Bot practice:** the offline bot match is now "Bot practice", with a Solo / Duo / Squad button next to the
  difficulty. Both settings are remembered, and in Duo or Squad bots fill your team.

**Not built in C6a:**

- **Downed and revive:** players are eliminated outright, with no knocked-down state that a teammate can revive. That
  needs a down state in the shared rules, a crawl, a revive action and a bleed-out timer. Left for after the platform
  side (C6c) so it can be judged in real team play.
- **Teams online:** online matches stay Solo until C6c brings parties, team queues and team results to the API.
  `ManagedServer.BuildResult` still reports Solo placements.

**Also fixed:** `unity-batch.sh test` could report the previous run's results when a run never started (a compile
error). It now deletes the old results file first, so a failed run fails.

### Tests (C6a)

| Suite          | Result                                                                                                                                                                                                                                                            |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET           | 134/134. New: teams fill in order and teammates start together; shots pass through a teammate and hit the enemy behind; a team places only when its last member is out, members share it, and the last team standing shares 1st; frags spare the team; fill bots complete a lone player's Squad, and Welcome and snapshots carry the team; bots never fire at a teammate; a whole Duo bot match gives every team one shared, distinct placement |
| Unity EditMode | 66/66. New: the bot practice mode is remembered and only Solo, Duo or Squad                                                                                                                                                                                       |
| Unity PlayMode | 24/24, including both full-platform tests. New: Squad bot practice gives you three bot teammates on your team                                                                                                                                                     |

## Part C6b (23 October 2026): the plane and parachute drop

**In the shared rules:**

- **Drop phases:** `MotorState.Drop` is Plane, Freefall, Parachute or None.
- **Free fall:** falls at 40 m/s and steers across the ground at 14 m/s.
- **Parachute:** opens on its own 45 m above whatever is below, or earlier with Jump from 60 m up. Under it you fall
  at 6 m/s and steer at 7 m/s.
- **Landing:** on the ground or a roof, ready to walk. Someone who comes down in the lake or river is set on the
  nearest dry ground.
- **Prediction:** falling is ordinary movement (`Simulation.Step`), so clients predict it like walking.
- **The full fall:** from 160 m it takes about 10 s.

**In the match** (`Server/MatchDrop.cs`, when `MatchConfig.PlaneDrop` is on):

- **Boarding:** when play starts, everyone boards a plane crossing the map in a straight line at 160 m and 60 m/s.
- **Flight path:** different every match (from the zone seed), wholly over the map, and passing between the map
  centre and the first circle.
- **Jumping:** Jump leaves the plane. Anyone still aboard at the end of the line is dropped.
- **Restrictions:** aboard or falling, there are no weapons, items or cars.
- **Where it's on:** offline city bot practice and online city matches. The small maps keep their spawn points.

**Protocol 11:**

- **Player flags:** now 16 bits (InPlane, Freefall, Parachute), so everyone sees who is falling or under a parachute.
- **Plane:** snapshots carry its position while it flies.

**Bots:**

- **Landing spot:** a bot picks a ground-level, walkable loot spot within 35 % of the coming circle's radius of its
  centre, so roofs can't strand it and bots land close enough to meet.
- **Jumping:** a bot jumps when the plane is right over that spot or has just passed its closest point, then steers
  to it while falling.

**In the game:**

- **Plane:** a placeholder airframe (fuselage, wings, tail) with a low engine drone.
- **Riding:** you're hidden aboard, and the camera rides above the plane.
- **Parachutes:** an orange canopy appears over anyone under a parachute.
- **HUD:** "[Space] / JUMP Jump out of the plane", then the height and when the parachute opens.

**What the drop did to matches** (simulator, 20 bots, loot-first, 6 matches each):

| Map | Start | Active play | Player kills | Zone eliminations |
| --- | --- | --- | --- | --- |
| v1 | spawn points | 7.4 min | 8.0 | 11.2 |
| v1 | drop | 6.7 min | 14.3 | 4.7 |
| v2 | spawn points | 7.3 min | 7.2 | 11.8 |
| v2 | drop | 7.3 min | 12.8 | 6.2 |

The drop fixes the problem C3 and C5 kept running into: most bots dying in the zone instead of fighting. Two earlier
tries at the bots' landing choice did worse:

- **Any loot spot within 70 % of the circle:** 5.0 kills on v2. Roofs stranded bots, and they spread too thin to meet.
- **Ground-only spots within 70 %:** 5.8 kills.
- **Clustering within 35 %:** 12.8 kills, the version above.

### Changed files (C6b)

- Shared: `MotorState.cs` (`DropPhase`), `GameRules.cs` (drop constants), `Simulation.cs` (falling, parachute,
  landing, `NearestDry`), `Server/MatchDrop.cs` (new: plane, jump, eject), `Server/Match.cs`, `MatchConfig.cs`
  (`PlaneDrop`), `Protocol/Packets.cs` and `Protocol/SnapshotCodec.cs` (16-bit flags, plane), `Bots/BotBrain.cs`
  (drop), `Server/LocalMatch.cs`.
- Server: `Platform/ManagedServer.cs` (drop on city maps), `Program.cs` (`--no-drop`).
- Unity: `OnlineGame.cs`, `OnlineViews.cs` (plane, parachutes), `OnlineHud.cs`, `ClientPrediction.cs`,
  `RemoteInterpolator.cs` (plane sampling).

### Tests (C6b)

| Suite          | Result                                                                                                                                                                                                                                                                               |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| .NET           | 139/139. New: free fall opens the parachute at 45 m and lands ready to walk in the expected time; Jump opens it early only above 60 m; free fall steers faster than the parachute; a landing in the lake ends on dry ground; everyone starts aboard a plane flying over the map, rides with it, can't shoot after jumping, is seen falling in snapshots, is dropped at the end of the line, and lands; bots all jump, land, and none is stranded on a roof; the full player entry is now 25 bytes |
| Unity EditMode | 66/66                                                                                                                                                                                                                                                                                |
| Unity PlayMode | 25/25, including both full-platform tests. New: an offline city match starts in the plane, which is drawn and ridden; Jump leaves it, a parachute opens on the way down, and you land                                                                                              |

### Not done in C1

- Bots are the Phase 8 bots: no difficulty levels or weapon choice by range yet (C5).
- Spawns are the map's fixed spawn points until the plane and parachute drop (C6).
- No minimap, compass or new items yet (C2, C3).
- Characters are capsules (Phase B assets).
