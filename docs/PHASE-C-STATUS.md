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

### Not done in C1

- Bots are the Phase 8 bots: no difficulty levels or weapon choice by range yet (C5).
- Spawns are the map's fixed spawn points until the plane and parachute drop (C6).
- No minimap, compass or new items yet (C2, C3).
- Characters are capsules (Phase B assets).
