# Capacity plan

Status: **model from local measurements**. Every number below comes from the dev machine (loopback, one process each). Re-measure on the chosen cloud instance type (ADR 0005) before ordering capacity, and replace the assumptions marked ⚠.

## What we measured

| Component              | Test                                                                | Result                                                                            |
| ---------------------- | ------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| Match server           | Phase 2 soak: one 30-player match, 3% loss, 60 ms latency, 30 Hz    | Tick avg 1.2 ms, p99 2.7 ms, max 17.4 ms (budget 33 ms); 39.9 kbps out per player |
| Match server, city map | Phase 4 performance tests                                           | 5 µs per movement step, 4 µs per line-of-sight ray on 3,871 boxes                 |
| Platform API           | Phase 6 load test: 200 players, 60 s, one Node process + PostgreSQL | 1,662 req/s, p95 175 ms, p99 240 ms, 0 errors                                     |
| Backup / restore       | Phase 6 drill                                                       | 132 KB dump of a small database; dump + restore 3 s                               |

## Model

**Match servers.** The tick loop uses about 4% of one core per 30-player match at p99 (2.7 of 33 ms on the 100 m map). The city adds line-of-sight work.

- ⚠ Assume **4 concurrent matches per vCPU**, leaving headroom for the city, GC pauses and noisy neighbours. Tick p99 must stay under the 16 ms budget on the Operations dashboard; that's how this assumption gets checked.
- Bandwidth: 30 × 40 kbps = **1.2 Mbps outbound per match**. A 4-vCPU server running 16 matches needs about 20 Mbps outbound.
- A match lasts about 8–10 minutes plus a queue; ⚠ assume **5 matches per match slot per hour**.

**Platform API.** A menu player makes about 1 request every 10 s; ⚠ in a match, about 1 every 60 s (only telemetry; gameplay goes to the match server).

- One API process handled 1,662 req/s at p95 175 ms. ⚠ Plan **5,000 concurrent players per API instance** (about 3× headroom), with 2 instances minimum for availability.
- The API is stateless. Rate limits are per instance (in memory); move them to Redis before running more than 2 instances.

**PostgreSQL.** Matchmaking uses row locks (`SKIP LOCKED`). Writes per match: results, ledger, participants and telemetry.

- ⚠ Start with a managed instance with 2 vCPU and 8 GB RAM, plus point-in-time recovery.
- Watch connection count: 10 per API instance today.

## Sizing by concurrent players (CCU)

| Peak CCU | Players in matches (⚠ 70%) | Matches (30 players) | Match-server vCPU (4 per vCPU) | API instances | Outbound bandwidth |
| -------- | -------------------------- | -------------------- | ------------------------------ | ------------- | ------------------ |
| 1,000    | 700                        | 24                   | 6                              | 2             | ~30 Mbps           |
| 5,000    | 3,500                      | 117                  | 30                             | 2             | ~140 Mbps          |
| 20,000   | 14,000                     | 467                  | 117                            | 4             | ~560 Mbps          |

Add 50% headroom for launch spikes. Scale match servers by **queue time p95** (Beta metrics) and **hosting share** (Operations): above 85% hosting means servers are saturated.

## Cost

The Operations dashboard computes cost per match and per concurrent-player hour from real server-minutes:

```
cost per match = server-hours × SERVER_COST_PER_HOUR_INR ÷ completed matches
```

Set `SERVER_COST_PER_HOUR_INR` to the hourly price of the chosen match-server instance; the default ₹25 is a placeholder. Track the trend weekly in the economy review: cost per match must stay below the revenue per match target set by the product owner.

## Re-measure before launch

1. Repeat the 30-bot soak 10× on the cloud instance type, with the city map, and record tick p99 and bandwidth.
2. Run `load-test.sh` against staging with 2 API instances behind the load balancer.
3. Fill in this table with the measured numbers; remove the ⚠ assumptions they replace.
