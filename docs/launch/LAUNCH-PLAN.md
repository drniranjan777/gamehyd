# Launch plan: staged rollout with rollback thresholds

Status: **plan**. The launch has not happened. It needs the Phase 6 release gates signed (BRD §14) and production infrastructure (ADR 0005).

## Preconditions (go / no-go)

| Gate                                    | Evidence                                                                      | Owner         |
| --------------------------------------- | ----------------------------------------------------------------------------- | ------------- |
| Crash-free sessions ≥ 99.5% in beta     | Admin portal → Beta metrics, last 7 days, ≥ 5,000 sessions                    | Unity lead    |
| Match completion ≥ 95%                  | Beta metrics                                                                  | Backend lead  |
| Zero purchase duplication               | Beta metrics → purchase integrity 0 / 0 / 0; sandbox purchases on both stores | Backend lead  |
| No open critical/high security findings | External pen test report; dependency audit clean                              | Security lead |
| Legal review complete                   | Privacy, terms, retention, grievance officer, age rating signed               | Product owner |
| On-call ready                           | Rotation staffed for 4 weeks; runbooks rehearsed on staging                   | Ops lead      |
| Capacity                                | [Capacity plan](CAPACITY-PLAN.md) provisioned for wave 3 plus 50%             | DevOps        |

## Waves

The game is published to the stores with staged rollout; each wave is recorded in **Admin portal → Releases** with its threshold, so reconciliation can halt it automatically.

| Wave | Audience                             | Store rollout | Hold at least           | Advance when                                                                   |
| ---- | ------------------------------------ | ------------- | ----------------------- | ------------------------------------------------------------------------------ |
| 0    | Internal + beta testers (India)      | closed track  | 7 days                  | Release gates green                                                            |
| 1    | India, Android                       | 5%            | 72 h and 5,000 sessions | Crash-free ≥ 99.5%, completion ≥ 95%, API p95 ≤ 300 ms, no Sev 1/2 open        |
| 2    | India, Android + iOS                 | 20%           | 72 h                    | Same, plus queue time p95 ≤ 60 s and payer conversion not falling              |
| 3    | India                                | 50% → 100%    | 7 days                  | Same; cost per match within the capacity plan                                  |
| 4    | Further countries (product decision) | per country   | 7 days per country      | Region servers up (ADR 0005), localisation, store and legal checks per country |

## Rollback thresholds

| Signal                                 | Halt (automatic or on-call)                                                                                                         | Roll back                                        |
| -------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------ |
| Crash-free sessions of the new version | Below the release's `minCrashFree` (default 99.5%) after `minSessions` (default 500): **automatic HALT** at the next reconciliation | Below 98%, or any crash loop on start            |
| Match completion                       | Below 95% for 2 hours                                                                                                               | Below 90% for 1 hour                             |
| API                                    | p95 > 300 ms or 5xx > 0.5% for 15 minutes                                                                                           | 5xx > 2% for 15 minutes                          |
| Server tick                            | Minutes over tick budget rising for 30 minutes                                                                                      | Matches terminating (server crash runbook)       |
| Purchases                              | Any purchase-integrity count above 0                                                                                                | Turn the store off (kill switch) and investigate |

"Halt" means pause the store rollout and stop advancing. "Roll back" follows [the rollback runbook](../runbooks/rollback.md). Server-side problems are fixed by rolling the server back, not the client.

## During each wave

- On-call checks **Beta metrics**, **Operations** and **Releases** at the start and end of each shift.
- Moderation and support work the report queue daily; target first response within 1 working day.
- Daily 15-minute launch stand-up: gates, incidents, player feedback, decision to hold or advance.

## After launch

Write the [launch report](LAUNCH-REPORT-TEMPLATE.md) within 2 weeks of wave 3; feed the findings into the [post-launch backlog](POST-LAUNCH-BACKLOG.md).
