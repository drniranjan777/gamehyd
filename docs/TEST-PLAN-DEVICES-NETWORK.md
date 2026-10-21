# Device and network test plan (Phase 0 draft)

Status: **proposed** — device list and thresholds need product-owner sign-off (BRD Appendix A).

## Proposed device matrix

| Tier | Android                                       | iOS       | Target (BRD §6, provisional)                     |
| ---- | --------------------------------------------- | --------- | ------------------------------------------------ |
| Low  | Redmi 13C or Galaxy A15 (Helio G85/G99, 4 GB) | iPhone 11 | Playable on Low preset, ≥ 30 FPS in light combat |
| Mid  | Redmi Note 13 or Galaxy A35 (6–8 GB)          | iPhone 13 | **30 FPS sustained** on Medium                   |
| High | OnePlus 12 or Galaxy S24                      | iPhone 15 | 60 FPS on High where thermals allow              |

Selection rule: the top-selling models in India in the prior 12 months for each price band. Re-check before content lock.

## What to measure per device

Frame time (avg, p95, p99), memory peak, thermal state after 20 min, startup time, scene-load time, crash-free sessions, battery drain per match. Captured by the Unity build smoke-test pipeline (Phase 1) and reported per build.

## Network profiles

| Profile   | RTT                | Jitter | Loss | Purpose                          |
| --------- | ------------------ | ------ | ---- | -------------------------------- |
| Broadband | 20–40 ms           | 5 ms   | 0%   | Baseline                         |
| Good 4G   | 60 ms              | 15 ms  | 1%   | Typical player                   |
| Poor 4G   | 150 ms             | 40 ms  | 3%   | Must stay playable               |
| Bad link  | 250 ms             | 80 ms  | 8%   | Degrades but does not desync     |
| Drop-out  | 3–15 s full outage | —      | —    | Reconnect within grace (BRD §14) |

Simulation tools: `clumsy` (Windows) or `tc netem` (Linux) on the server host; Android Network Link Conditioner / iOS NLC on devices.

## Server-side acceptance (from Phase 0 onwards)

| Test                                                                    | Where it lives today                                            | Status                                               |
| ----------------------------------------------------------------------- | --------------------------------------------------------------- | ---------------------------------------------------- |
| 8-player full match, one server-decided winner                          | `UdpMatchTests` (8 bots over UDP)                               | Automated, passing                                   |
| 30-player smoke match                                                   | `RWBR.MatchServer --players 30 --fast` + `RWBR.Bots --count 30` | Run manually, passing                                |
| Speed hack, input bursts, replays, fire-rate abuse, LOS, forged packets | `MatchAuthorityTests`                                           | Automated, passing                                   |
| Reconnect within grace / token single-use / elimination after grace     | `MatchAuthorityTests`                                           | Automated, passing                                   |
| 20–30 players for a full-length match (10–15 min), repeated 10×         | —                                                               | Phase 2: needs default config + longer bot behaviour |
| Same under each network profile                                         | —                                                               | Phase 2: add loss/jitter injection to the bot client |
