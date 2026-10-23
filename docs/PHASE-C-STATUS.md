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

### Not done in C1

- Bots are the Phase 8 bots: no difficulty levels or weapon choice by range yet (C5).
- Spawns are the map's fixed spawn points until the plane and parachute drop (C6).
- No minimap, compass or new items yet (C2, C3).
- Characters are capsules (Phase B assets).
