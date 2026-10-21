# ADR 0001 — Repository layout and tooling

- **Status:** Accepted (22 Oct 2026)
- **Context:** BRD v1.2 §8 proposes a monorepo; nothing existed yet.

## Decision

One Git repository using the BRD §8 layout:

| Path                     | Tooling                                                            |
| ------------------------ | ------------------------------------------------------------------ |
| `apps/api`               | NestJS 11, TypeScript, Jest                                        |
| `apps/web`, `apps/admin` | Next.js 15 (App Router), React 19                                  |
| `database/`              | Plain SQL migrations + a small TypeScript runner (ADR 0004)        |
| `packages/shared-config` | Shared `tsconfig` base                                             |
| `game/dedicated-server`  | .NET (`RWBR.Shared`, `RWBR.MatchServer`, `RWBR.Bots`, xUnit tests) |
| `game/unity-client`      | Unity 6 project                                                    |
| `infrastructure/`        | Docker Compose (local), later IaC and monitoring                   |
| `docs/`                  | Architecture, ADRs, plans, runbooks                                |

- JavaScript workspaces use **pnpm 9** (`pnpm-workspace.yaml`), Node ≥ 22, Prettier.
- `packages/api-contracts` (OpenAPI → generated TS and C# types) is created in Phase 3 when the first real endpoints land.
- CI: GitHub Actions (`.github/workflows/ci.yml`), one job for the JS workspace (with a PostgreSQL 18 service), one for .NET. Unity CI is added once a Unity build license is available.

## Consequences

- One place for contracts and cross-cutting changes; a single PR can change the protocol, server and client together.
- Unity's `Library/` and .NET `artifacts/` are gitignored; Unity `.meta` files are committed.
