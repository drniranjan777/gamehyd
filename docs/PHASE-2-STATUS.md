# Phase 2 status — 22 October 2026

Phase 2 goal (PHASE-PLAN.md): a fair, cheat-resistant match from start to winner, proven with 8 and then 20–30 players. The code is done and tested on the dev machine (loopback with simulated impairment). **Phase 2 is not complete**: it still needs a real-network test on the chosen cloud region (ADR 0005), real phones, and the netcode library benchmark (ADR 0003).

## Exit gate

| Gate item                                                               | Status               | Evidence                                                                                |
| ----------------------------------------------------------------------- | -------------------- | --------------------------------------------------------------------------------------- |
| Repeatable 8-player match                                               | **Done**             | `UdpMatchTests`, `ImpairedNetworkTests` (automated)                                     |
| 20–30-player proof with CPU, memory, bandwidth, tick stability recorded | **Done on loopback** | 30-bot fast runs and a full-length 30-bot soak (table below)                            |
| Latency/jitter/loss and tampering tests                                 | **Done (simulated)** | Bot impairment (loss, latency, jitter, reordering); 46 .NET tests including cheat cases |
| Unity client plays online                                               | **Done**             | Online scene; PlayMode test joins the real server, predicts, gets the result            |
| Real network / real devices                                             | **Not done**         | Needs a cloud account and phones                                                        |

## What was built

**Bandwidth (protocol v3):**

- Snapshots are quantised (1 cm positions, 16-bit yaw) and delta-encoded against the last snapshot each client acknowledged (the `AckTick` already in its inputs, so no extra ack packets). Unchanged player: 3 bytes; moving: 9; full: 21.
- Configurable snapshot rate (`--snapshot-every N`).
- The recipient's own position and vertical speed are sent at full precision, so prediction reconciles exactly.

| 30 bots, loopback                            | Outbound per player |
| -------------------------------------------- | ------------------- |
| Phase 1 (full float snapshots, 30 Hz)        | 127 kbps            |
| Delta, 30 Hz                                 | **45 kbps**         |
| Delta, 15 Hz (`--snapshot-every 2`)          | **22 kbps**         |
| Delta, 30 Hz, 5% loss + 75 ms + 40 ms jitter | 46 kbps             |

**Security (ADR 0007):** signed single-use join tickets (expiry, match id, replay protection, account takeover on a fresh ticket) and 8-byte HMAC tags on every input/leave packet and on reconnects. Open dev mode when no secret is configured.

**Impairment testing:** bots simulate loss, latency and jitter in both directions, including reordering.

**Unity online client (`Assets/Scenes/Online.unity`):**

- `NetClient`: UDP, delta decoding with its own snapshot history, RTT estimate, inbound kbps, auto-reconnect from a new socket after 3 s of silence (mobile network changes).
- `ClientPrediction`: inputs applied immediately with the shared rules, rebased on each snapshot, unacknowledged inputs replayed; corrections blended visually (snap only above 2 m).
- `RemoteInterpolator`: other players rendered 4 ticks (133 ms) behind the newest snapshot, with drift correction; the viewed tick goes back to the server as `AckTick` (lag-compensation target and delta baseline).
- Adopts the server's map version (40 m fast map or 100 m map) or refuses to play on a mismatch.
- HUD: connect screen (server address, name), waiting/countdown/alive, zone ring and outside-zone warning, server-driven vitals and ammo, hit and kill markers from server state, kill feed, result screen, network overlay (RTT, kbps, delta %, prediction corrections).
- Practice pause menu → "Play online"; online pause menu → "Leave match".

## Tests

| Suite          | Result                                                                                                                                                                                                                                       |
| -------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET           | 46/46: codec (exactness, sizes, quantisation, missing baselines), tickets (verify, tamper, wrong secret, expired, wrong match, reuse), packet tags, signed reconnect, server delta baselines, 8-bot matches under poor 4G and in ticket mode |
| Unity EditMode | 12/12: prediction exact through latency, jumps and stairs; correction blending; interpolation timing; address parsing                                                                                                                        |
| Unity PlayMode | 4/4: includes **online end to end** (real server + bot processes): join, map adoption, 7.5 m predicted walk with 0 corrections, 33 ms RTT on loopback, 74/76 delta snapshots, server result                                                  |

## Soak

`infrastructure/scripts/soak.sh 30 0.03 60 30`: one full-length match on the 100 m map with production zone timings. 30 bots, each with 3% loss, 60 ms latency and 30 ms jitter in both directions. Dev laptop, loopback.

| Measure                   | Result                                                                                       |
| ------------------------- | -------------------------------------------------------------------------------------------- |
| Outcome                   | One server-decided winner; all 30 bots received the result                                   |
| Active play               | 298 s (5 min); 9,134 ticks                                                                   |
| Tick rate achieved        | 30.0 Hz                                                                                      |
| Tick time avg / p99 / max | 1.2 / 2.7 / 17.4 ms (budget 33 ms)                                                           |
| Outbound per player       | 39.9 kbps                                                                                    |
| Snapshots                 | 271,813 delta / 206 full; 26 missing baselines (recovered)                                   |
| Integrity                 | 0 malformed packets, 0 bad tags, 427 inputs dropped by the backlog cap (bursts after jitter) |

Still to do: repeat 10× on the chosen cloud region with real phones in the mix (BRD §14).

## Not done in Phase 2 (and why)

- **Cloud and real-network runs** (ADR 0005): need an account and region. The bots and report are ready to run there.
- **Netcode library benchmark** (ADR 0003): still open. The custom stack now meets the bandwidth target, which weakens the case for switching.
- **Reliable event channel**: eliminations and results are still covered by repeated state, not acknowledgements.
- **Weapon balance in `Welcome`**: the client assumes the default rifle for cosmetic tracer cadence. The server's fast-mode rifle fires slightly faster, which affects visuals only.
- **Remote players' shots**: other players' tracers and muzzle flashes are not sent yet (needs a shot event).
- **Result submission to the API** (signed server-to-server): Phase 3, with the platform API.
