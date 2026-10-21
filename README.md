# Real-World Battle Royale

Commercial free-to-play mobile battle royale (Unity 6 client, fictional Hyderabad-inspired map). Requirements:
[BRD v1.2](Real_World_Battle_Royale_Detailed_BRD_Technical_Plan_v1.2.docx) · [Phase-wise delivery plan](PHASE-PLAN.md)

**Status: Phase 0 in progress.** Engineering scaffolding and the technical prototypes are built and tested on the dev machine; product decisions and sign-offs are still open. See [Phase 0 status](docs/PHASE-0-STATUS.md).

| Folder                    | What                                                                                                                                                            | Status                                                 |
| ------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------ |
| `apps/api/`               | Platform API (Node.js + TypeScript, NestJS)                                                                                                                     | Phase 0: health endpoints, error envelope, request IDs |
| `apps/web/`               | Next.js public website                                                                                                                                          | Phase 0: placeholder page                              |
| `apps/admin/`             | Next.js operations portal                                                                                                                                       | Phase 0: API health page; MFA/RBAC in Phase 3/5        |
| `database/`               | PostgreSQL migrations, runner, roles                                                                                                                            | Phase 0: runner + `0001_users`                         |
| `game/dedicated-server/`  | Authoritative C# match server, shared rules, headless bots                                                                                                      | Phase 0 prototype: 8- and 30-bot matches pass          |
| `game/unity-client/`      | Unity 6 client                                                                                                                                                  | Phase 0: greybox block + third-person controller       |
| `infrastructure/`         | Docker Compose (local PostgreSQL)                                                                                                                               | Written, not yet run (no Docker on the dev machine)    |
| `packages/shared-config/` | Shared TypeScript config                                                                                                                                        | In use                                                 |
| `docs/`                   | [Architecture](docs/ARCHITECTURE.md), [ADRs](docs/adr), [risk register](docs/RISK-REGISTER.md), [device & network test plan](docs/TEST-PLAN-DEVICES-NETWORK.md) |                                                        |

The React Native companion in the BRD is deferred; its features move to the Next.js website.

## Developer setup

Requirements: Node ≥ 22 with pnpm 9, .NET SDK 10, PostgreSQL 18 (or Docker), Unity 6000.6.7f1.

```bash
pnpm install
```

**Database** — see [database/README.md](database/README.md) for a local cluster on port 55432, then:

```bash
MIGRATOR_DATABASE_URL=postgres://rwbr_migrator@localhost:55432/rwbr_dev pnpm db:migrate
```

**API, web, admin**

```bash
DATABASE_URL=postgres://rwbr_app@localhost:55432/rwbr_dev pnpm dev:api   # http://localhost:3001/api/v1/health
pnpm dev:web                                                             # http://localhost:3000
pnpm dev:admin                                                           # http://localhost:3002
```

**Checks**

```bash
pnpm format:check && pnpm typecheck && pnpm build
TEST_ADMIN_DATABASE_URL=postgres://postgres@localhost:55432/postgres pnpm test
dotnet test game/dedicated-server/RWBR.MatchServer.slnx
```

**Match server + bots** (two terminals)

```bash
dotnet run --project game/dedicated-server/src/RWBR.MatchServer -c Release -- --players 8 --fast
dotnet run --project game/dedicated-server/src/RWBR.Bots -c Release -- --count 8
```

**Unity** — open `game/unity-client` in Unity 6000.6.7f1 and play `Assets/Scenes/Greybox.unity` (WASD + mouse, Shift to sprint). Headless:

```bash
game/unity-client/unity-batch.sh setup
game/unity-client/unity-batch.sh test EditMode
game/unity-client/unity-batch.sh test PlayMode
```
