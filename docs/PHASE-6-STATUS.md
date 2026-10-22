# Phase 6 status — 22 October 2026

Phase 6 goal (PHASE-PLAN.md): alpha and closed beta, hardening, and the BRD §14 release gates. **The engineering for measuring and hardening the beta is built and tested.** The beta itself has not started. It needs a cloud environment (ADR 0005), store builds, India test cohorts, a device lab, an external penetration test, legal sign-off and an on-call rotation: people and infrastructure, not code.

## What was built

**Telemetry and beta KPIs (migration 0008).**

- `POST /telemetry/events` takes batched client events with a strict schema per type: session start/end, crash, FPS sample, disconnect. It works before sign-in, so boot crashes count; a valid token attaches the account.
- Free-form fields are rejected. Raw events are purged after `TELEMETRY_RETENTION_DAYS` (90).
- Queue time is now recorded per match participant.
- `GET /admin/metrics?days=N` (new permission `metrics.read`) computes every KPI the plan lists: crash-free sessions, FPS by device (time-weighted average and 5th-percentile), match completion, disconnects per 100 joins, queue time p50/p95, D1/D7 retention, payer conversion, plus purchase integrity.
- It also evaluates the release gates: crash-free ≥ 99.5%, completion ≥ 95%, zero duplicate or unpaid grants and zero ledger drift. Security findings, legal review and on-call are shown as MANUAL gates.
- The admin portal has a **Beta metrics** page.

**Unity:**

- **Telemetry client:** batches every 30 s and on pause/quit. Crashes come from unhandled exceptions (5 per session at most), there's a 60 s FPS window, and disconnects are reported on reconnect attempts. Events are kept in memory while offline. It's off in batch mode.
- **Accessibility:** interface size 0.8–1.5× for the HUDs and menus, visual gunshot direction arrows around the crosshair (for deaf or hard-of-hearing players), and reduced flashing.

**Security hardening:**

- **Sanctions now apply to live access tokens.** Account status is re-checked on every authenticated request (cached 5 s), so a ban, suspension or completed deletion takes effect in seconds, not after the 15-minute token expiry.
- **Security headers:** the API sends nosniff, a deny-all CSP for its JSON, no-store, X-Frame-Options and HSTS. The admin portal and website have CSPs and permission policies.
- **Authorization matrix test:** every operation in the OpenAPI contract (74 today) is called without credentials and with the wrong kind (admin token on player routes, player token on admin and server routes). New endpoints are covered automatically.
- **Token tampering tests:** a modified JWT payload and `alg: none` are rejected.
- **Dependency audit:** PostCSS (bundled in Next.js) had 4 advisories; overridden to a patched version.

**Operations:**

- **Unhosted-match sweeper** (risk 16): FORMING matches whose server never confirms hosting are cancelled after 45 s and their players requeued. It runs from queue polling and the reconciliation job.
- **Weapon kill switch end to end:** heartbeat assignments carry `disabledWeapons`. The match server withdraws that loot before the match starts and picks another starting weapon, or none if all are off.
- **Load test** (`infrastructure/scripts/load-test.sh`): 200 simulated players for 60 s against one API process and PostgreSQL on the dev machine. 100,582 requests at 1,662 req/s, p50 74 ms, p95 175 ms, p99 240 ms, no errors (target p95 ≤ 300 ms, errors ≤ 0.1%).
- **Backup and restore drill** (`backup-restore-test.sh`): a database with players, a purchase, a refund and audit rows is dumped and restored into a new database in 3 s. Row counts match in every table, migrations are recorded, there's no ledger drift, and append-only triggers still refuse deletes.
- **Runbooks** (`docs/runbooks`): matchmaking outage, match server crash, payment verification outage, exploit, data incident, release rollback, plus severity levels and everyday tools.

## Tests

| Suite          | Result                                                                                                                                                                  |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| API            | 111/111 (was 46): 63 authorization-matrix cases, token tampering, ban on live token, security headers, sweeper, telemetry validation, KPI and release-gate calculations |
| Database       | migrations 0001–0008                                                                                                                                                    |
| .NET           | 71/71 (was 67): kill switch picks a new starting weapon, all-disabled starts unarmed, no disabled loot on the city, heartbeat JSON                                      |
| Unity EditMode | 25/25: telemetry JSON and escaping, sound-cue directions and fading, accessibility settings bounds                                                                      |
| Unity PlayMode | 8/8: the store test also posts a real telemetry batch to the API (accepted by the strict schema)                                                                        |
| Scripts        | full-stack smoke, load test and backup-restore drill passed                                                                                                             |

## Release gates (BRD §14): where they stand

| Gate                           | Status                                                                                        |
| ------------------------------ | --------------------------------------------------------------------------------------------- |
| Crash-free sessions ≥ 99.5%    | Measurable now; needs beta traffic                                                            |
| Match completion ≥ 95%         | Measurable now; needs beta traffic                                                            |
| Zero purchase duplication      | Enforced by the database; measured continuously; real-store sandbox test still open (Phase 5) |
| No open critical/high security | Internal tests pass; **external pen test not done**                                           |
| Legal review complete          | **Open**: privacy, terms, retention, grievance officer, age rating                            |
| On-call ready                  | Runbooks written; **rotation not staffed, runbooks not rehearsed on real infrastructure**     |

## Not done in Phase 6 (and why)

- **Closed beta and alpha cohorts, device compatibility on phones, real-network tests:** need store builds, cloud servers in India and test devices.
- **External penetration test, secrets manager, observability backend (metrics, traces, alerts):** need the cloud decision.
- **Crash reporting with native crashes:** the telemetry covers managed exceptions only; native crashes (ANR, signals) need Firebase Crashlytics or the Unity equivalent in a store build.
- **Match-server DRAINING mode, an in-game "matchmaking paused" screen, in-game news:** small follow-ups noted in the runbooks.
- **Accessibility audit with players:** the settings exist; colour-blind review, screen-reader support for menus and control remapping need the production UI (ADR 0006 replaces IMGUI).
- **Feature freeze and release candidate:** product decisions, after the beta data.
