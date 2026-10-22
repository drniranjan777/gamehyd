# ADR 0002 — Authoritative match server runtime

- **Status:** Proposed — needs Technical Director sign-off
- **Context:** BRD §7.1/§11 require a dedicated authoritative server separate from the REST API. Options: (a) Unity headless server build, (b) standalone .NET process sharing rules code with the Unity client, (c) Node.js.

## Decision (proposed)

**(b) Standalone .NET server** (`game/dedicated-server/src/RWBR.MatchServer`, currently `net10.0`; .NET 10 is the installed LTS SDK).

Game rules that must be identical on client and server live in **`RWBR.Shared`** (netstandard2.1, C# 9, no Unity or server APIs): movement, collision, hit geometry, weapon definitions, map layout, zone config and the network protocol. The .NET projects compile it normally; Unity compiles the _same source files_ as a local UPM package (`com.rwbr.shared`, referenced with `file:` from `Packages/manifest.json`). There is no copied DLL to drift.

## Evidence from the Phase 0 prototype

| Scenario             | Ticks | Tick time avg / p99 / max | Achieved rate | Outbound per player |
| -------------------- | ----- | ------------------------- | ------------- | ------------------- |
| 8 bots, fast config  | 162   | 0.46 / 8.7 / 10.6 ms      | 30.2 Hz       | 35 kbps             |
| 30 bots, fast config | 222   | 0.87 / 3.9 / 10.5 ms      | 30.1 Hz       | 126 kbps            |

Windows 11 dev machine, loopback, single process. These are smoke-test numbers, not a load test: the fast config ends matches in 3–5 s of active play.

## Why not the alternatives

- **Unity headless server:** physics/animation authority for free, but ~10× the memory per match, slower startup, a Unity build license per server image and harder unit testing. Revisit if server-side animation or Unity physics becomes necessary (e.g. vehicles).
- **Node.js:** single-threaded GC'd simulation with no shared code with the C# client; rules would be written twice.

## Consequences

- Map collision for the server must be exported from the Unity scene (or authored as data both sides read). Phase 0 uses `MapLayout` as the single source; Phase 4 needs an export step from Unity level geometry.
- Vertical movement (jump, crouch, stairs, roofs, falling) was added to `Simulation` in Phase 1 and runs identically in Unity and on the server.
