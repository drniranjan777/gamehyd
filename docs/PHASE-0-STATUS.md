# Phase 0 status — 22 October 2026

Engineering scaffolding and the technical prototypes for Phase 0 are in place and tested on the dev machine. **Phase 0 is not complete**: the product decisions, hosting tests and sign-offs below need people, accounts or devices.

## Exit gate (from PHASE-PLAN.md)

| Gate item                                                     | Status          | Evidence                                                                                                                                                                  |
| ------------------------------------------------------------- | --------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 8 clients move and shoot in one session with server authority | **Done (bots)** | `UdpMatchTests`: 8 bots over UDP play to one winner; 30-bot run also passes (ADR 0002 table)                                                                              |
| Repo, CI and local stack run for every engineer               | **Partly**      | Repo, pnpm workspace, tests and builds run locally. CI workflow written but **not run** (no GitHub remote). Docker Compose written but **not run** (Docker not installed) |
| All Appendix-A decisions signed                               | **Not started** | Needs product owner: title, age rating, launch countries, match size/duration, drop mechanic, device matrix, payments, privacy                                            |
| ADRs for netcode, server runtime, hosting, repo layout        | **Drafted**     | `docs/adr/0001`–`0005`; 0001 and 0004 accepted, 0002–0003 proposed, 0005 open                                                                                             |

## What exists

| Area         | What                                                                                                                                                                                                                                                              | Verified by                                                                                              |
| ------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| API          | NestJS: `GET /api/v1/health`, `/health/ready` (DB + schema version), BRD error envelope, request IDs                                                                                                                                                              | 4 Jest tests; ran against the migrated dev DB                                                            |
| Database     | Migration runner (checksums, advisory lock), `bootstrap/roles.sql`, `0001_users`                                                                                                                                                                                  | 4 integration tests on PostgreSQL 18.4                                                                   |
| Web          | Next.js public placeholder page                                                                                                                                                                                                                                   | `next build`                                                                                             |
| Admin        | Next.js operations portal showing live API health; no-index/no-frame headers                                                                                                                                                                                      | `next build`; rendered "Healthy — schema version 0001" against the running API                           |
| Match server | .NET authoritative UDP server: phases, zone, hitscan with lag compensation and LOS, reload/cadence, eliminations, winner, reconnect, JSON run report                                                                                                              | 20 xUnit tests                                                                                           |
| Bots         | Headless clients for load and match tests                                                                                                                                                                                                                         | 8- and 30-player runs                                                                                    |
| Shared rules | `RWBR.Shared`, compiled by both .NET and Unity from one source                                                                                                                                                                                                    | Unity EditMode test uses it                                                                              |
| Unity        | Unity 6000.6.7f1 project; greybox block (roads, enterable building with roof, apartments, market rows, cover) built from the server's map layout; third-person controller using server movement rules; over-the-shoulder camera with wall pull-in; landscape-only | EditMode 2/2 (every collider matches the server layout), PlayMode 1/1 (player walks through the doorway) |

## Not done in Phase 0 (and why)

- **Unity client is not connected to the match server yet** — Phase 2 work. Server authority is proven with bots.
- **No Android/iOS build or prototype video** — needs device matrix sign-off and build signing; the Android module is installed.
- **Touch controls** — Phase 1.
- **Rooftop access** — server movement is flat (risk 5).
- **Netcode library benchmark** (ADR 0003) and **hosting ping/cost tests** (ADR 0005).
- **Asset provenance register** — empty because no third-party assets are used yet; create one before the first purchase.

## How to run everything

See the README "Developer setup" section.
