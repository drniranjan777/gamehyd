# Runbooks

Short, tested procedures for the incidents named in PHASE-PLAN Phase 6. Each one says how you notice the problem, how to contain it in the first 15 minutes, how to fix it, and how to confirm the fix. They assume the admin portal, the platform API and PostgreSQL. Commands marked **(cloud)** depend on the hosting decision (ADR 0005) and are placeholders until staging exists.

| Runbook                                                       | Use when                                                        |
| ------------------------------------------------------------- | --------------------------------------------------------------- |
| [Matchmaking outage](matchmaking-outage.md)                   | Players stuck in the queue, no matches starting                 |
| [Match server crash](server-crash.md)                         | A server or match dies mid-game, completion rate drops          |
| [Payment verification outage](payment-verification-outage.md) | Purchases stay PENDING, 503 PLATFORM_UNAVAILABLE, refund storms |
| [Exploit or cheating wave](exploit.md)                        | A weapon, item or bug is being abused                           |
| [Data incident](data-incident.md)                             | Suspected leak, wrong data change, lost data                    |
| [Release rollback](rollback.md)                               | A release breaks the game, API or portal                        |

## Everyday tools

- **Admin portal → Beta metrics:** crash-free sessions, match completion, disconnects, queue time and the release gates.
- **Admin portal → Live ops:** kill switches for the store, matchmaking, individual products and weapons. They take effect within 5 seconds on every API instance; the weapon switch applies to matches assigned after the change. Every change is audited and needs a reason.
- **Admin portal → Orders → Run reconciliation now:** cancels stale pending orders, ends expired suspensions, cancels matches no server picked up, purges old telemetry and reports ledger drift.
- **Admin portal → Audit log:** who did what, filtered by entity, actor or action; append-only in the database.
- **API logs:** structured JSON with `requestId`, also returned to clients in every error; ask players for it.
- **Scripts:** `infrastructure/scripts/fullstack-smoke.sh` (end-to-end check), `backup-restore-test.sh` (restore drill), `load-test.sh` (API load).

## Severity

| Sev | Meaning                                                        | Response                                     |
| --- | -------------------------------------------------------------- | -------------------------------------------- |
| 1   | Game unplayable for most players, money or data at risk        | Page on-call now; update status every 30 min |
| 2   | A major feature broken (store, matchmaking region), workaround | On-call within 30 min                        |
| 3   | Degraded or minor; a few players affected                      | Next working day                             |

After every Sev 1 or 2: a blameless write-up within 5 working days, with the timeline (from the audit log and API logs), the cause, and the follow-ups as tickets.
