# Architecture (current state)

Target architecture: BRD v1.2 §7. This page describes what is built today.

```
 Unity client (C#)                    Next.js web / admin
   │  shares RWBR.Shared source          │ HTTPS (server-side fetch)
   │                                     ▼
   │  UDP (custom protocol)         NestJS API  /api/v1 ──── PostgreSQL 18
   ▼                                  (health only so far)     (rwbr_app role)
 .NET match server ◄─ RWBR.Shared
   (authoritative, in-memory state;
    no durable writes yet)
```

## Trust boundaries in place

- **Match server:** clients send inputs only. Movement, firing cadence, ammo, hits (with LOS and capped rewind), damage, eliminations, placements and the winner are server decisions. Malformed or unknown packets are counted and dropped.
- **API:** global error filter never leaks internal errors; request IDs on every response.
- **Database:** the API's role cannot run DDL, delete users or touch migration history.

## Not yet connected

- The match server does not report results to the API (Phase 2/3: signed server-to-server result submission).
- The Unity client does not talk to the API or the match server (Phase 2).
- No auth, matchmaking or Redis yet (Phase 3).
