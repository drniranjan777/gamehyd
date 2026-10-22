# Runbook: match server crash

**Symptoms:** players dropped mid-match; Beta metrics match completion falls (gate: ≥ 95%), disconnects per 100 joins rise; matches end TERMINATED; servers missing from Overview.

## What happens automatically

- When a crashed server restarts and heartbeats IDLE, the API marks its unfinished matches CANCELLED (still forming) or TERMINATED (in progress), and frees those players from the queue.
- Matches assigned to a server that never confirms hosting are cancelled after 45 s and the players requeued.
- No rewards are paid for terminated matches; nothing needs reversing.
- Clients try to reconnect for 20 s (reconnect grace) before giving up; each attempt is recorded as a DISCONNECT event.

## Contain

1. If many servers are crashing (a bad build or bad map data), pause matchmaking (**Live ops**) so players don't keep joining doomed matches.
2. If one weapon or item is involved (crash after a pickup, from the server logs), switch that weapon off in **Live ops → Weapons disabled**.

## Diagnose

- Server logs (`RWBR.MatchServer` writes JSON lines): the last `hosting match` and the exception. Each match logs `tickMsP99` and bandwidth when it ends normally.
- `SELECT game_server_id, count(*) FROM matches WHERE status = 'TERMINATED' AND ended_at > now() - interval '1 hour' GROUP BY 1;`: one bad host or all of them?
- Reproduce locally: `dotnet run --project game/dedicated-server/src/RWBR.MatchServer -c Release -- --simulate 5 --map hyd-city-v1 --players 30` runs whole matches in seconds.

## Fix

Roll the server build back ([rollback](rollback.md)), or ship the fix. Server builds are independent of the client as long as the protocol version is unchanged.

## Confirm

Match completion ≥ 95% over the next 100 matches; no new TERMINATED matches from the affected hosts.
