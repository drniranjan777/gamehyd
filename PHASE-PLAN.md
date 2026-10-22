# Real-World Battle Royale — Phase-Wise Delivery Plan

Source: BRD v1.2, 21 Oct 2026 (client document, kept privately outside this repository). Prepared 21 Oct 2026.

**Starting point:** fresh build, started 22 Oct 2026. **Current progress:** Phases 0–7 engineering work done and tested locally, plus the post-launch P1 engineering items (Phase 8); the launch itself waits on content (art, animation, audio), store accounts, cloud/staging, beta cohorts, device measurements, an external pen test, legal review and on-call. See the status docs in [docs/](docs/) (PHASE-0 … PHASE-8-STATUS.md), the [runbooks](docs/runbooks/README.md) and the [launch plan and backlog](docs/launch/LAUNCH-PLAN.md).

**Tech stack (confirmed):**

| Layer                      | Technology                                                                  |
| -------------------------- | --------------------------------------------------------------------------- |
| Game client                | Unity 6 + C# (Android + iOS)                                                |
| Authoritative match server | C# — Unity headless server build _or_ standalone .NET 8 (decide in Phase 0) |
| Platform API               | Node.js + TypeScript (NestJS)                                               |
| Database                   | PostgreSQL 16–18 (managed in production)                                    |
| Web + admin portal         | Next.js (TypeScript)                                                        |
| Supporting (optional)      | Redis for queues/presence/rate limits; object storage + CDN for assets      |

---

## 1. Review of the BRD

### What is strong

- Clear trust boundary: server owns combat, inventory, currency, results. Client sends inputs only.
- Realistic scope: 20–30 players for MVP, 50 is a later gate, 100 is out of scope.
- Economy is designed correctly: append-only ledger, idempotency keys, server-side receipt verification, no pay-to-win.
- Good legal guardrails: fictional Hyderabad-inspired map, no Google Maps 3D extraction, asset provenance register.
- Measurable acceptance criteria and release gates (Section 14, 20).

### Gaps and issues to resolve

| #   | Issue                                                                                                                                                  | Why it matters                                                                             | Recommendation                                                                                                                               |
| --- | ------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------- |
| 1   | **Match server runtime not decided**                                                                                                                   | It's the highest-risk component; doc only says "dedicated authoritative process"           | Use C#, not Node.js: share weapon/movement/damage code with the Unity client. Choose Unity headless server vs .NET 8 in Phase 0 by benchmark |
| 2   | **Netcode library not chosen**                                                                                                                         | Affects prediction, reconciliation, lag compensation, cost                                 | Benchmark 2 options in Phase 0 (e.g. Unity Netcode for GameObjects, Fish-Net, Mirror, or custom UDP) with 2–8 clients                        |
| 3   | **Server hosting not chosen**                                                                                                                          | Drives latency for India and monthly cost                                                  | Shortlist AWS Mumbai (GameLift or EC2/Agones) vs GCP Mumbai; decide with ping + cost-per-match data                                          |
| 4   | **React Native companion** is in the doc but not in the confirmed stack                                                                                | Extra codebase, extra QA                                                                   | Drop from MVP; companion features (profile, match history, events) go on the Next.js website instead                                         |
| 5   | Redis is "recommended" but not in confirmed stack                                                                                                      | Matchmaking queue/presence needs fast ephemeral state                                      | Start with PostgreSQL + in-memory; add Redis only when load tests prove the need (doc already says this)                                     |
| 6   | Analytics, crash reporting, push notifications have no named tools                                                                                     | Beta metrics (crash-free ≥99.5%, D1/D7 retention) can't be measured without them           | Pick in Phase 0: e.g. Firebase Crashlytics/Analytics or Unity Analytics, plus OpenTelemetry + Grafana for servers                            |
| 7   | Payment specifics missing                                                                                                                              | Google Play Billing and Apple StoreKit 2 have different server-verification flows and fees | Document both flows in Phase 3; premium currency design is still an open Appendix-A decision                                                 |
| 8   | Repository layout differs between BRD §8 (`apps/`, `game/`) and current README (`Backend/`, `app/`)                                                    | Confuses the team and CI paths                                                             | Pick one layout in Phase 0 and update the BRD                                                                                                |
| 9   | BRD v1.1 (§9, revision note) and README describe `Backend/`, `app/`, 16 migrations and "100 API tests passing" as if built — **none of it exists yet** | Stakeholders may think work is further along than it is                                    | **Fixed 21 Oct:** README and BRD v1.2 now mark them as planned; the 47-table design and migrations are built in Phase 3                      |
| 10  | Phases 1–5 overlap heavily (weeks 5–26)                                                                                                                | Only works with a full parallel team                                                       | Timeline below assumes ~10–14 people; with a smaller team expect 12–14 months                                                                |
| 11  | No tutorial/onboarding UX or anti-cheat tooling detail                                                                                                 | Retention and fairness risks                                                               | Covered in Phase 4 and Phase 6 below                                                                                                         |
| 12  | Device matrix not named                                                                                                                                | Every FPS/thermal target depends on it                                                     | Name 3 Android (low/mid/high) + 2 iOS devices in Phase 0                                                                                     |

---

## 2. Phase-Wise Plan

Each phase ends with a demo, evidence (tests, builds, metrics), and sign-off before the next phase expands scope.

### Phase 0 — Discovery & Technical Validation (Weeks 1–4)

**Goal:** lock decisions and prove the riskiest parts are feasible.

> **Progress (22 Oct 2026):** repo, CI workflow, API, database runner, web/admin, C# match server + bots and the Unity greybox are built and tested. Open: Appendix-A decisions, hosting tests, netcode library benchmark, device builds. Details: [docs/PHASE-0-STATUS.md](docs/PHASE-0-STATUS.md).

| Track          | Work                                                                                                                                                                                                                                                                                             |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Product        | Signed GDD, feature priority list, match rules, control scheme, art style guide, release definition, all Appendix-A decisions                                                                                                                                                                    |
| Unity          | Greybox city block: one road, one enterable building, rooftop, cover, moving third-person character                                                                                                                                                                                              |
| Networking     | Prototype 2–8 clients against a C# authoritative server; compare 2 netcode options; decide tick-rate strategy                                                                                                                                                                                    |
| Repo & tooling | `git init`, scaffold the monorepo from scratch (BRD §8 layout: `apps/web`, `apps/admin`, `apps/api`, `game/unity-client`, `game/dedicated-server`, `packages/api-contracts`, `database/migrations`, `infrastructure/`, `docs/`), branch rules + code review, CI skeleton, lint/format/type-check |
| Backend        | Empty NestJS app with health endpoint, first PostgreSQL migration (users) + migration runner, Docker Compose (API + PostgreSQL), `.env.example`                                                                                                                                                  |
| Web            | Empty Next.js apps for website and admin, deployed to staging                                                                                                                                                                                                                                    |
| Infra          | Hosting shortlist and India ping tests; choose analytics and crash tools                                                                                                                                                                                                                         |
| Legal          | Asset provenance register, licensing review for any third-party asset                                                                                                                                                                                                                            |

**Deliverables:** Architecture Decision Records (netcode, server runtime, hosting, repo layout), prototype video/build, device matrix, risk register, revised budget.

**Exit gate:** 8 clients move and shoot in one session with server authority; all Appendix-A decisions signed; repo, CI and local Docker stack run for every engineer.

---

### Phase 1 — Core Gameplay Vertical Slice (Weeks 5–10)

> **Progress (22 Oct 2026):** practice mode built on shared rules — jump/crouch/stairs/roofs, rifle with spread/recoil/reload, armor, medkits, loot, 5 AI enemies, HUD, inventory, touch controls, quality presets, frame-time logging. Open: mid-tier device FPS measurement, crash reporting, art pipeline. Details: [docs/PHASE-1-STATUS.md](docs/PHASE-1-STATUS.md).

**Goal:** single-player game that feels good on a real phone.

**Unity (C#):**

- Character movement (walk, sprint, crouch, jump), third-person camera, touch controls + sensitivity settings
- One weapon: fire, ammo, reload, recoil, spread, hit feedback
- Health, armor, healing, loot pickup, basic inventory UI
- Small map loop using the first modular building kit
- Graphics quality presets (low / mid / high)

**Art pipeline:** modular meshes, materials, collision, LODs, lighting strategy, mobile texture budgets.

**DevOps:** automated Unity Android/iOS builds, smoke tests, frame-time + memory + crash capture.

**Exit gate:** playable slice running at ≥30 FPS on the named mid-tier device; crash reporting live.

---

### Phase 2 — Authoritative Multiplayer (Weeks 8–16, overlaps Phase 1)

> **Progress (22 Oct 2026):** Unity plays online with prediction/reconciliation and interpolation; protocol v3 delta snapshots (45 kbps/player at 30 players); signed join tickets + packet tags; impairment-tested 8-bot matches and a full-length 30-bot soak. Open: cloud/real-network runs, devices, library benchmark. Details: [docs/PHASE-2-STATUS.md](docs/PHASE-2-STATUS.md).

**Goal:** a fair, cheat-resistant match from start to winner.

**Match server (C#):**

- Session lifecycle and match state machine: queued → forming → countdown → active → final zone → completed / cancelled
- Input validation: movement envelope, speed, teleport distance, input sequence, fire cadence, ammo, reload state, range, line of sight
- Server-authoritative shooting, damage, armor, elimination, loot spawn, safe-zone controller, winner/placement
- Snapshots, client prediction, interpolation, reconciliation, lag compensation (defined rewind window)
- Disconnect/reconnect with grace period and single-use reconnect tickets
- Result submission to the Node.js API (signed, server-to-server)

**Testing:** latency/jitter/packet-loss simulation, client-tampering tests, headless bot clients for load.

**Exit gate:** repeatable 8-player match, then 20–30-player load test passing with CPU, memory, bandwidth, tick stability recorded.

---

### Phase 3 — Platform Backend & Database (Weeks 10–18, overlaps Phase 2)

> **Progress (22 Oct 2026):** exit gate passes end to end locally — guest auth with rotating refresh tokens, profile/stats/history, inventory, reports, PostgreSQL matchmaking, signed server heartbeats and results, exactly-once ledger rewards; Unity Quick play. Open: staging/cloud, platform sign-in credentials, secure device storage. Details: [docs/PHASE-3-STATUS.md](docs/PHASE-3-STATUS.md).

**Goal:** durable accounts, progression, and inventory on staging.

**Node.js + TypeScript (NestJS) — `/api/v1`:**

- Auth: guest login, platform login (Google Play Games / Apple), token refresh with rotation, logout
- Player: profile, stats, match history (cursor pagination)
- Maps: list, versioned asset manifest
- Matchmaking: join/leave queue, status, server allocation, client-version check
- Matches: result ingestion from match server, reconnect ticket, result view
- Inventory and loadouts, rewards, XP, missions foundation
- Reports (player reporting)
- OpenAPI contract → generated types for Next.js and C# client
- Standard error envelope, rate limits, request IDs, schema validation, log redaction

**PostgreSQL:**

- Versioned migrations for the 47-table design (users, profiles, sessions, maps, matches, catalog, inventory, wallets + append-only ledger, orders, payments, missions, social, moderation, live-ops config, servers, admin + audit)
- Least-privilege roles (`migrator` vs `app` — app cannot run DDL or edit ledger/audit rows)
- Idempotency keys on every currency/entitlement/result mutation
- Seeds for local/staging; no position ticks stored in the database

**Ops:** staging environment, structured logs, metrics, tracing, secrets manager.

**Exit gate:** integration tests pass on staging; player plays a match and sees XP and history persist; forged results rejected.

---

### Phase 4 — Content, Map & Progression (Weeks 14–24)

> **Progress (22 Oct 2026):** Hyderabad-inspired city (hyd-city-v1, 5 districts, 3,871 boxes, chunked LOD), 6-weapon roster with slots/headshots/falloff/pellets, protocol v4, placeholder 3D audio with occlusion, main menu, loadout/profile screens, 7-step tutorial, TTK table and match simulator. Open: art, animation, final audio, production UI, bot navigation, drop/spawn decision. Details: [docs/PHASE-4-STATUS.md](docs/PHASE-4-STATUS.md).

**Goal:** content-complete alpha.

- Expand the fictional Hyderabad-inspired map: named districts, markets, apartment blocks, rooftops, landmarks, interiors, loot paths, safe-zone pathing, distant LOD/impostors, chunk streaming
- Full weapon roster (5–8 classes) with server-controlled, versioned balance data
- Character animations, original/licensed audio (attenuation, indoor/outdoor, footsteps)
- Lobby, results screen, cosmetics equip UI, onboarding and tutorial
- Playtest-driven tuning: zone timing, loot distribution, match duration, weapon balance

**Exit gate:** alpha candidate — one map, solo mode, full match loop, no release-blocking collision holes or soft-locks.

---

### Phase 5 — Store, Web/Admin & Economy (Weeks 18–26)

**Goal:** verified purchases and safe operations tooling.

**Node.js API:**

- Store catalog, platform SKU mapping, order lifecycle (database-enforced state machine)
- Google Play and Apple server-to-server receipt verification + real-time developer notifications / App Store Server Notifications
- Entitlement grants, wallet ledger, restore purchases, refund handling, reconciliation job
- Season-pass foundations, live events scheduling, kill switches

**Next.js:**

- **Public website:** landing page, news/events, player profile and match history (replaces the RN companion), privacy policy, terms, account deletion, support
- **Admin portal (MFA + RBAC):** overview dashboard, player search, sanctions, match lookup, catalog editor + publish approval, orders + reconcile, refunds, report moderation, append-only audit viewer
- Roles: support, moderator, economy operator, analyst, super-admin

**Unity:** store UI, purchase flow via Unity IAP, inventory refresh from server.

**Testing:** duplicate webhooks, retries, delayed callbacks, refunds, forged receipts.

**Exit gate:** end-to-end sandbox purchase on Android and iOS grants exactly once; every admin action audited.

---

### Phase 6 — Alpha, Closed Beta & Hardening (Weeks 24–34)

- Internal alpha → closed beta with India cohorts
- Device compatibility, accessibility, security (API authorization, replay, tampering, external pen test), load, soak, and backup-restore tests
- Track: crash-free sessions, FPS by device, match completion, disconnects, queue time, D1/D7 retention, payer conversion
- Fix by severity; feature freeze before release candidate
- Runbooks: matchmaking outage, server crash, payment verification outage, exploit, data incident, rollback

**Exit gate (BRD §14 release gates):** crash-free ≥99.5%, match completion ≥95%, zero purchase duplication, no open critical/high security issues, legal review complete, on-call ready.

---

### Phase 7 — Launch & Live Ops (Weeks 34–40+)

- Staged rollout by region/cohort with rollback thresholds
- Dashboards: cost per match, cost per concurrent player, API p95/p99, server tick health
- On-call rotation, incident response, player support, moderation
- Content calendar, season pass, economy reviews
- Revisit 50-player mode, squads, friends/parties only after retention and cost data

**Deliverables:** launch report, live-ops calendar, post-launch backlog, capacity plan.

---

## 3. Timeline Overview

```
Week:        1    5    10   15   20   25   30   35   40
Phase 0      ████
Phase 1           ██████
Phase 2              █████████
Phase 3                 █████████
Phase 4                       ███████████
Phase 5                            █████████
Phase 6                                  ███████████
Phase 7                                            ███████→
```

Planning window: 8–10 months with a full team; 10–12 months is the safer estimate. Budget per BRD: ₹1.2–2.5 crore for the first release.

## 4. Suggested Team per Track

| Track                        | People                       |
| ---------------------------- | ---------------------------- |
| Unity client                 | 2–3 engineers                |
| Match server / netcode (C#)  | 1–2 engineers                |
| Node.js API + PostgreSQL     | 1–2 engineers                |
| Next.js web + admin          | 1 engineer                   |
| 3D art, animation, audio     | 3–4 (in-house or outsourced) |
| QA + automation              | 1–2                          |
| DevOps / SRE                 | 1                            |
| Product owner, game designer | 1–2                          |

## 5. Decisions Needed Before Phase 0 Ends

1. Match-server runtime: Unity headless vs .NET 8
2. Netcode library / transport
3. Cloud provider and India region
4. Device matrix (3 Android + 2 iOS)
5. Analytics and crash reporting tools
6. Premium currency design and payment providers
7. Final title, age rating, launch countries, languages
8. Drop/spawn mechanic and match duration
9. Confirm repository layout — BRD §8 recommended, since nothing exists yet to migrate
