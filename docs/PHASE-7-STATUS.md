# Phase 7 status — 22 October 2026

Phase 7 goal (PHASE-PLAN.md): launch and live operations. **The live-ops tooling is built and tested: staged-release guardrails, operations and cost dashboards, the season pass and economy reviews. The deliverables exist as a plan, a capacity model, a calendar, a backlog and a report template.** The launch itself has not happened. It waits on the Phase 6 release gates and the P0 items in the [post-launch backlog](launch/POST-LAUNCH-BACKLOG.md): cloud, store accounts, pen test, legal, content and on-call.

## What was built

**Operations dashboards** (`GET /admin/ops`, portal → Operations; migration 0009):

- **API latency.** Every request is timed by route template into a fixed-bucket histogram. Each instance writes one row per route per minute, so p50/p95/p99 combine correctly across instances and minutes. The error rate comes from 5xx responses.
- **Match-server tick health.** Servers now send their 99th-percentile tick time over the last ~10 s with every heartbeat; one sample per server per minute is stored. The dashboard shows the median and worst tick p99 and the minutes over budget (`TICK_BUDGET_MS`, 16 ms).
- **Capacity and cost.**
  - Peak and average concurrent players, server-hours and hosting share.
  - Estimated cost, cost per completed match and cost per concurrent-player hour (`SERVER_COST_PER_HOUR_INR`; set it from the chosen instance).
- **Retention.** Samples are purged after `OPS_RETENTION_DAYS` (30).

**Staged releases** (`/admin/releases`, portal → Releases):

- Each store rollout is recorded with its platform, version, rollout %, regions and rollback threshold (minimum crash-free sessions after N sessions).
- Its health comes from telemetry for that exact version.
- Reconciliation **halts a release automatically** when it breaks its threshold, writes `release.auto_halt` to the audit log, and points on-call to the rollback runbook.
- Resume, complete and rolled-back changes are audited with a reason.

**Season pass** (`/seasons/current`, `/seasons/current/claim`; `/admin/seasons`; portal → Seasons; Unity → Season pass):

- Seasons are drafted with a reward track (free and premium per tier), then published. Published seasons are frozen by triggers and can't overlap (an exclusion constraint).
- **Season XP** is earned from match XP, in the same transaction as the match reward, so it's exactly once.
- **Claims** are explicit; the `season_claims` primary key means each reward pays out once. Premium rewards need the pass.
- **The pass is an ordinary store product** with a new `SEASON_PASS` grant, so it uses the same two-person approval, receipt verification and exactly-once grant.
- **Refunding the pass** removes premium access, revokes premium cosmetics and claws back premium currency (any shortfall is recorded); free rewards stay.

**Economy review** (`GET /admin/economy`, portal → Economy):

- Coins and gems issued and removed by reason, the sink ratio, circulating supply, holders, and median and p90 balances.
- Sales and refunds by product and channel, and season participation.
- It's all read from the ledger, so it always agrees with balances.

**Deliverables** (`docs/launch`):

- [Launch plan](launch/LAUNCH-PLAN.md): go/no-go gates, waves (India Android 5% → 20% → 100%, then more countries), rollback thresholds.
- [Capacity plan](launch/CAPACITY-PLAN.md): sizing model from the measured soak and load tests; assumptions marked for cloud re-measurement.
- [Live-ops calendar](launch/LIVE-OPS-CALENDAR.md): first 12 weeks, two seasons.
- [Post-launch backlog](launch/POST-LAUNCH-BACKLOG.md): P0/P1/P2 from all phases.
- [Launch report template](launch/LAUNCH-REPORT-TEMPLATE.md).

## Tests

| Suite          | Result                                                                                                                                                                                                                                                                                                                  |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| API            | 124/124 (was 111). Season lifecycle: publish, freeze, overlap refused, XP, tier locks, single claims, premium via a purchased pass, refund revokes. Ops dashboard from real heartbeats and request timings. Automatic release halt and audit. Economy report. The authorization matrix now covers the 10 new operations |
| .NET           | 73/73: tick health in heartbeats (JSON) and from a running host                                                                                                                                                                                                                                                         |
| Unity EditMode | 26/26: season pass DTOs, including the gap between seasons                                                                                                                                                                                                                                                              |
| Unity PlayMode | 8/8                                                                                                                                                                                                                                                                                                                     |
| Browser check  | Admin portal with live data: Seasons, Releases, Economy, Operations (per-route p50/p95/p99 from real traffic)                                                                                                                                                                                                           |

## Not done in Phase 7 (and why)

- **The launch, on-call rotation, player support and moderation staffing:** need the release gates and people.
- **Alerting:** the thresholds are defined and computed, but paging needs the observability backend (cloud).
- **XP multiplier for event weekends, in-game news:** small follow-ups the live-ops calendar asks for.
- **50-player mode, squads, friends/parties:** by plan, only after retention and cost data from a real launch.
