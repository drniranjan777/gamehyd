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

## Platform flow (Phase 3)

1. **Client → API (HTTPS):** guest sign-in, then `POST /matchmaking/queue` and poll `GET /matchmaking/status`.
2. **Match server → API (HMAC-signed):** heartbeat every 2 s. An idle server receives the next FORMING match (id, map, capacity, minimum players, fill wait).
3. Once the server reports it is hosting, status returns a **seat**: `host:port`, a signed join ticket and a session key (ADR 0007).
4. **Client → match server (UDP):** ticket in `Hello`, HMAC tag on every input.
5. **Match server → API (signed):** final result. The API checks it against the roster, then writes placements, XP, stats and coins (ledger) in one transaction, exactly once.
6. **Client → API:** profile, stats and history show the result.

## Not yet in place

- Staging/cloud deployment, secrets manager, metrics/tracing backend (ADR 0005).
- Google Play Games / Apple sign-in (credentials pending); admin portal auth (Phase 5).
- Redis: not needed yet (matchmaking uses PostgreSQL row locks; rate limits are per instance).
