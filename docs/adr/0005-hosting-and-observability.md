# ADR 0005 — Hosting region and observability tools

- **Status:** Open — decisions needed from the product owner and technical director
- **Context:** BRD §7.3 and §18. Nothing here can be decided from the dev machine: it needs ping tests from target players' networks, vendor quotes and account set-up.

## Shortlist

| Need                       | Options                                                                                                | How to decide                                                                                          |
| -------------------------- | ------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------ |
| Game servers (India)       | AWS `ap-south-1` Mumbai (EC2 / GameLift), GCP `asia-south1` Mumbai (GKE + Agones), Azure Central India | Ping from Jio/Airtel/Vi 4G and home broadband in 3+ cities; cost per match-hour from the bot load test |
| API + PostgreSQL           | Same cloud as game servers; managed PostgreSQL 18 with PITR (RDS / Cloud SQL / Azure Flexible Server)  | Follow game-server choice                                                                              |
| Crash reporting            | Firebase Crashlytics, Sentry, Unity Cloud Diagnostics                                                  | Unity IL2CPP symbol support, cost at beta volume                                                       |
| Product analytics          | Firebase Analytics, Unity Analytics, GameAnalytics                                                     | Funnel and retention cohorts (BRD §18), data-residency terms                                           |
| Server metrics/logs/traces | OpenTelemetry → Grafana (Cloud or self-hosted) / cloud-native                                          | Cost at beta log volume                                                                                |

## Recommendation (to confirm)

AWS Mumbai for the first closed beta (largest India footprint, GameLift if fleet management is wanted), OpenTelemetry + Grafana for servers, Crashlytics + Firebase Analytics for the client. Re-check after the ping and cost tests.

## Needed from the business

Cloud account owner, budget cap for staging, and approval to create accounts.
