# Real-World Battle Royale

Commercial free-to-play mobile battle royale (Unity 6 client, fictional Hyderabad-inspired map). Requirements:
[BRD v1.2](Real_World_Battle_Royale_Detailed_BRD_Technical_Plan_v1.2.docx) · [Phase-wise delivery plan](PHASE-PLAN.md)

**Status: Phase 5 engineering done (store, verified purchases, refunds, admin portal with MFA/RBAC/audit, public website); real store billing waits on Google Play / App Store accounts.** Phases 0–5 are built and tested locally. Status: [Phase 0](docs/PHASE-0-STATUS.md) · [Phase 1](docs/PHASE-1-STATUS.md) · [Phase 2](docs/PHASE-2-STATUS.md) · [Phase 3](docs/PHASE-3-STATUS.md) · [Phase 4](docs/PHASE-4-STATUS.md) · [Phase 5](docs/PHASE-5-STATUS.md).

| Folder                    | What                                                                                                                                                            | Status                                                                                    |
| ------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| `apps/api/`               | Platform API (Node.js + TypeScript, NestJS)                                                                                                                     | Phase 5: + store, purchase verification, webhooks, refunds, admin API, kill switches      |
| `apps/web/`               | Next.js public website                                                                                                                                          | Phase 5: landing, news/events, player profiles, support, privacy/terms (drafts), deletion |
| `apps/admin/`             | Next.js operations portal                                                                                                                                       | Phase 5: MFA sign-in, RBAC, players, reports, catalogue approval, orders, audit, live ops |
| `database/`               | PostgreSQL migrations, runner, roles                                                                                                                            | Phase 5: migrations 0001–0007, ledger, orders, payments, admin RBAC, audit                |
| `game/dedicated-server/`  | Authoritative C# match server, shared rules, headless bots                                                                                                      | Phase 3: managed mode (heartbeat, assigned matches, signed results); 30-bot soak          |
| `game/unity-client/`      | Unity 6 client                                                                                                                                                  | Phase 5: city, tutorial, online + Quick play, menus, store and account screens            |
| `infrastructure/`         | Docker Compose (local PostgreSQL)                                                                                                                               | Written, not yet run (no Docker on the dev machine)                                       |
| `packages/shared-config/` | Shared TypeScript config                                                                                                                                        | In use                                                                                    |
| `docs/`                   | [Architecture](docs/ARCHITECTURE.md), [ADRs](docs/adr), [risk register](docs/RISK-REGISTER.md), [device & network test plan](docs/TEST-PLAN-DEVICES-NETWORK.md) |                                                                                           |

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

**API, web, admin** — copy `apps/api/.env.example` values into your environment (dev-only secrets).

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

**Whole platform locally** — API + managed match server + bots, then check XP/history in the database:

```bash
pnpm --filter @rwbr/api build
TEST_ADMIN_DATABASE_URL=postgres://postgres@localhost:55432/postgres infrastructure/scripts/fullstack-smoke.sh
```

**Store and admin portal (dev)** — the API's `.env.example` turns on dev purchases. Create the first admin, then sign in at http://localhost:3002 with the password and a code from an authenticator app:

```bash
pnpm --filter @rwbr/api build
cd apps/api && ADMIN_PASSWORD='choose-a-long-password' node --env-file=.env.example dist/admin-cli.js --email you@example.com --name "Your Name" --role SUPER_ADMIN
```

In Unity, set `RWBR_DEV_PURCHASE_SECRET` (same value as the API's `DEV_PURCHASE_SECRET`) before starting the editor to buy INR items through the dev store.

To play it yourself: run the API (above), then `RWBR_SERVER_SHARED_SECRET=… RWBR_TICKET_SECRET_HEX=… dotnet run --project game/dedicated-server/src/RWBR.MatchServer -c Release -- --api-url http://localhost:3001/api/v1 --address 127.0.0.1:7777 --fast` with the same secrets as the API, and use **Quick play** in the Online scene.

**Online play (dev server, no platform)** — start a server (above, e.g. `--players 2 --fast`) and one bot, then in Unity open `Assets/Scenes/Online.unity` (or Practice → Esc → Play online) and connect to `127.0.0.1:7777`. Bots can simulate bad networks: `--loss 0.05 --latency 75 --jitter 40`.

**Balance tools** — `docs/balance/TTK.md` (regenerate: `UPDATE_DOCS=1 dotnet test game/dedicated-server/RWBR.MatchServer.slnx`), and full-match simulation: `dotnet run --project game/dedicated-server/src/RWBR.MatchServer -c Release -- --simulate 3 --map hyd-city-v1 --players 30`.

**Unity** — open `game/unity-client` in Unity 6000.6.7f1 and play `Assets/Scenes/MainMenu.unity` (WASD + mouse, Shift sprint, Space jump, C crouch, E pick up, Q / 1 / 2 / wheel switch weapon, R reload, H heal, Tab bag, Esc menu). Headless:

```bash
game/unity-client/unity-batch.sh setup
game/unity-client/unity-batch.sh test EditMode
game/unity-client/unity-batch.sh test PlayMode
game/unity-client/unity-batch.sh android   # development APK in game/unity-client/Builds/Android
```
