# ADR 0003 — Netcode and transport

- **Status:** Proposed — prototype done, benchmark against one library still to do
- **Context:** BRD §7.2/§11: UDP-style transport for high-frequency gameplay, clients send inputs only, server snapshots, prediction/reconciliation, lag compensation, reconnect.

## What the prototype uses

A small custom protocol over raw UDP (`RWBR.Shared/Protocol`):

- **Client → server:** `Hello` (protocol version, session token for reconnect), `Input` (newest + 2 previous inputs for loss tolerance: sequence, last seen server tick, move, yaw/pitch, buttons), `Leave`.
- **Server → client:** `Welcome` (player ID, rotating session token, tick rate, map version), `Reject`, full `Snapshot` every tick (with the recipient's last processed input sequence for reconciliation), `Eliminated`, `MatchCompleted`.
- **Authority:** one input consumed per player per tick; inputs older than the last processed sequence are dropped (replay protection); backlog capped at 4; move vectors clamped; fire cadence, ammo and reload enforced server-side; hits tested against target positions rewound to the shooter's acknowledged tick, capped at 200 ms; walls block shots.
- **Reconnect:** silent clients are marked disconnected after 3 s and eliminated after the grace period (20 s default). A `Hello` with the last token from any address restores the player and rotates the token; the old token is rejected.

Covered by 20 automated tests (`game/dedicated-server/tests`), including speed-hack, input-burst, replay, fire-rate, line-of-sight, forged-packet, reconnect and late-join cases, plus an 8-bot match over real UDP.

## Still open before accepting

1. **Benchmark one library** against the custom stack: Unity Netcode for GameObjects 2.13 (bundled with the editor) or Fish-Net. Criteria: bandwidth at 30 players, server CPU, how cleanly authority stays server-side, licence, and whether it can run outside a Unity server build (ADR 0002).
2. **Security:** UDP packets are unauthenticated and unencrypted. Before any external test: API-issued, signed, single-use join tickets (`POST /matches/{id}/reconnect-ticket` already in BRD §10) and per-session packet authentication (HMAC or DTLS).
3. **Bandwidth:** full snapshots cost ~126 kbps/player at 30 players and 30 Hz. Delta compression, quantised positions and relevance filtering are needed for mobile data (target < 50 kbps).
4. **Reliability:** `Eliminated` and `MatchCompleted` are resent by state (snapshot flags, repeated result) instead of an ack/resend channel. Needs a small reliable-ordered channel for events.

## Recommendation

Keep the custom protocol for Phase 1–2 unless the library benchmark wins clearly. It is small, fully tested and keeps rules in shared C#.
