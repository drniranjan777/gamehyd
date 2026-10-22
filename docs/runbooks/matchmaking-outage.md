# Runbook: matchmaking outage

**Symptoms:** players report endless "Searching"; Beta metrics shows queue time p95 rising and no new matches; Overview shows 0 live matches or 0 servers online.

## Contain (first 15 minutes)

1. Check **Overview → Servers online**. If it's 0, the match servers aren't heartbeating: go to [server crash](server-crash.md).
2. If servers are online but matches aren't starting, pause matchmaking so players aren't stuck: **Live ops → Matchmaking open: off**, with a reason. Queue requests then get `503 MATCHMAKING_DISABLED` (the game shows "Matchmaking is paused for maintenance").
3. Post a status message as a **Live ops event** (title "Matchmaking paused", ending in about an hour). It shows on the website and in the game's home-screen news.

## Diagnose

- `SELECT status, count(*) FROM matches WHERE created_at > now() - interval '30 minutes' GROUP BY 1;` Many FORMING matches with `hosted_at IS NULL` means servers are assigned but never confirm hosting. The sweeper cancels those after `UNHOSTED_MATCH_TIMEOUT_SECONDS` (45 s) and requeues the players; check that it runs (reconciliation report: `unhostedMatchesCancelled`).
- `SELECT address, status, reported_state, last_heartbeat_at FROM game_servers ORDER BY last_heartbeat_at DESC;` shows stale heartbeats and servers stuck HOSTING.
- API logs: errors on `/internal/game-servers/heartbeat` (bad signatures mean the server and API secrets don't match after a rotation).
- Clients rejected with `CLIENT_OUTDATED`: `MIN_CLIENT_VERSION` was raised before the new build reached the stores.

## Fix

- Restart or replace unhealthy servers **(cloud)**. They re-register on the first heartbeat.
- Secret mismatch: deploy the same `SERVER_SHARED_SECRET` and `TICKET_SECRET_HEX` to the API and the servers.
- Wrong `MIN_CLIENT_VERSION`: lower it and restart the API.

## Confirm

Turn matchmaking back on. Within 5 minutes Overview shows live matches, and Beta metrics shows queue time p50 back under 30 s. Cancel the status event.
