# ADR 0007 — Join tickets and packet authentication

- **Status:** Accepted for implementation (22 Oct 2026). The match server side is done; the API side is Phase 3.
- **Context:** BRD §7.2 (short-lived tokens, never trust the client), §10 (`POST /matches/{matchId}/reconnect-ticket`, tickets single-use and bound to player + match), ADR 0003 open item 2.

## Flow

1. The client asks the API for a seat (matchmaking, Phase 3) over HTTPS.
2. The API returns `{ server: "host:port", ticket: <base64>, sessionKey: <base64> }`. The ticket is signed with a secret shared only by the API and the match servers. The session key is derived from the same secret and **is never inside the ticket**.
3. The client sends the ticket in its UDP `Hello`. The server checks the signature, expiry, match id and single use, then derives the same session key.
4. Every client→server `Input` and `Leave` packet carries an 8-byte tag: the first 8 bytes of `HMAC-SHA256(sessionKey, packet)`. Untagged or wrongly tagged packets are dropped (`BadTags` counter).
5. A reconnect `Hello` (session token from the last `Welcome`) must also be tagged, so a sniffed token alone cannot take over a player. A fresh ticket for the same account also takes over the existing player (app restart, new device).

Servers started without a secret run in **open dev mode** (no tickets, no tags). The Unity client, bots and tests use it today.

## Ticket format v1 (all integers little-endian)

| Bytes | Field                                               |
| ----- | --------------------------------------------------- |
| 1     | format version = `0x01`                             |
| 16    | ticket id (random; .NET `Guid` byte order)          |
| 16    | match id (`Guid`)                                   |
| 16    | account id (`Guid`)                                 |
| 4     | expiry, Unix seconds (`u32`)                        |
| 1 + n | display name: length byte + UTF-8, at most 24 bytes |
| 32    | `HMAC-SHA256(secret, all bytes above)`              |

Session key = first 16 bytes of `HMAC-SHA256(secret, ASCII("rwbr-session-v1") ‖ ticketId bytes)`.

Reference implementation and tests: `game/dedicated-server/src/RWBR.Shared/Protocol/JoinTicket.cs`, `tests/.../Phase2NetworkTests.cs` (`TicketTests`). The Node.js issuer must produce byte-identical tickets for the test vectors. Note that .NET `Guid` bytes are mixed-endian, so convert UUID strings with care.

## Limits (accepted for now)

- Server→client packets are not tagged. A spoofed snapshot could only confuse that one client's display; the server's state is unaffected.
- Packets are authenticated, not encrypted. Gameplay data is not secret, and nothing personal goes over UDP: the display name is the only personal field.
- Ticket expiry should be short (≤ 2 minutes). Clock skew between the API and the servers must be under 30 s (NTP).
- Secret rotation: run with two secrets (current and next) during rotation. Not implemented yet.
