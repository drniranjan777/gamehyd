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

### Not done in C1

- Bots are the Phase 8 bots: no difficulty levels or weapon choice by range yet (C5).
- Spawns are the map's fixed spawn points until the plane and parachute drop (C6).
- No minimap, compass or new items yet (C2, C3).
- Characters are capsules (Phase B assets).
