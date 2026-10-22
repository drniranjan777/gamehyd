# ADR 0008 — Maps as versioned, generated data shared by server and client

- **Status:** Accepted (22 Oct 2026)
- **Context:** BRD 5.4 (stable chunk IDs and versions; a match references an immutable map build), risk 4 (client/server geometry mismatch), Phase 4 (bigger city, LOD, chunk loading).

## Decision

1. **One source of truth.** Every playable map is a `MapLayout` in `RWBR.Shared`: boxes for collision and line of sight, a kind per box, loot, spawns and districts. The match server simulates on it and Unity renders it. There is no separate level file to keep in sync.
2. **Generated, deterministic.** `hyd-city-v1` is produced by `CityMap` from a fixed seed with our own xorshift PRNG (`System.Random` is not guaranteed identical across runtimes), plus a reusable building kit (walls with doorways, roofs, outside or inside stairs).
3. **Immutable versions.** A published version never changes:
   - a test pins the SHA-256 of `hyd-city-v1`'s geometry, loot and spawns, and migration 0006 records the same hash;
   - any generator change must ship as `hyd-city-v2`, while matches in progress keep `v1`.
4. **Fast queries.** A uniform 16 m spatial grid serves movement and line-of-sight queries: 5 µs per movement step and 4 µs per ray on 3,871 boxes, and a test checks it gives the same answers as a brute-force scan.
5. **Client rendering.** Boxes merge into 64 m chunk meshes with a two-level LOD (everything up close, tall silhouettes far away, culled beyond), plus mesh colliders for the camera. A Unity test raycasts box tops and checks the heights against the server layout.

## Consequences

- Artists cannot yet place assets by hand; real buildings will need a pipeline. The likely path: model in a DCC tool → export collision boxes into a `MapLayout` asset → the same hash/version rules apply. The kit and kinds here define what that pipeline must export.
- The generator is code, so reviewing a map change means reviewing a diff plus the hash change.
- Water is a 1.3 m collision barrier rendered flat. Swimming is out of scope.
