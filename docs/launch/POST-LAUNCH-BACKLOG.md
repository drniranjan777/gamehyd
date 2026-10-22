# Post-launch backlog

Collected from the open items of Phases 0–7, ordered by what blocks or risks the business first. Severity: **P0** blocks launch, **P1** first month after launch, **P2** later.

## P0: blocks launch (needs people, accounts or infrastructure)

| Item                                                                    | From    | Notes                                                            |
| ----------------------------------------------------------------------- | ------- | ---------------------------------------------------------------- |
| Cloud provider, regions, staging and production (ADR 0005)              | Phase 0 | Everything below depends on it                                   |
| Google Play Console + App Store Connect accounts, products, Unity IAP   | Phase 5 | Exit gate: sandbox purchase on both stores, granted exactly once |
| Google Play Games / Game Center sign-in                                 | Phase 3 | Account recovery across devices; restore after reinstall         |
| Secure credential storage on device (Keystore / Keychain)               | Phase 3 | Guest secret is in PlayerPrefs today (risk 15)                   |
| Secrets manager; rotate dev secrets                                     | Phase 3 | Risk 14                                                          |
| External penetration test                                               | Phase 6 | Release gate                                                     |
| Legal review (privacy, terms, retention, grievance officer, age rating) | Phase 5 | Release gate; pages are marked as drafts                         |
| Native crash reporting (Crashlytics or equivalent)                      | Phase 6 | Crash-free gate must include native crashes (risk 22)            |
| Art, animation, final audio, production UI                              | Phase 4 | Content-complete; replaces greybox and IMGUI                     |
| Drop/spawn mechanic and loot-first start                                | Phase 4 | Matches end in 1–5 min on the city today (risk 17)               |
| On-call rotation staffed; runbooks rehearsed on staging                 | Phase 6 | Release gate                                                     |

## P1: first month

| Item                                                                      | Notes                                              |
| ------------------------------------------------------------------------- | -------------------------------------------------- |
| Redis for rate limits and the status cache when running > 2 API instances | In-memory today                                    |
| Match-server DRAINING mode for rolling deploys                            | Schema has the status; server doesn't implement it |
| In-game report button on the results screen                               | API exists                                         |
| In-game news and a "matchmaking paused" screen                            | Website shows events today                         |
| XP multiplier live setting (double-XP weekends)                           | Needed by the live-ops calendar                    |
| Bot navigation and looting                                                | Needed to validate loot balance in the simulator   |
| Cosmetics drawn on characters                                             | Loadout is stored but not rendered                 |
| Alerting on the Phase 7 thresholds (pager, not just dashboards)           | Needs the observability backend                    |

## P2: after retention and cost data (PHASE-PLAN Phase 7)

- 50-player mode, squads, friends and parties: only if retention and cost per match support it.
- Leaderboards and missions.
- Second map version.
- Accessibility: control remapping, colour-blind review, screen-reader support for menus (with production UI).
- Netcode library benchmark (ADR 0003), if real-network results disappoint.
