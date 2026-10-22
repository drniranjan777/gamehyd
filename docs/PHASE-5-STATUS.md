# Phase 5 status — 22 October 2026

Phase 5 goal (PHASE-PLAN.md): verified purchases and safe operations tooling. **The engineering is built and tested end to end with a dev store, a mock of Google's APIs and a generated Apple-style certificate chain.** The exit gate ("sandbox purchase on Android and iOS grants exactly once") is **not yet met**: it needs Google Play Console and App Store Connect accounts, Unity IAP and test devices.

## What was built

**Database: migration 0007.**

- Store products with a DRAFT → PENDING_APPROVAL → PUBLISHED → RETIRED lifecycle, a database-enforced two-person rule and frozen published prices.
- Orders with a database-enforced state machine, payment transactions unique per platform transaction, and append-only purchase events.
- Admin users (scrypt passwords, AES-GCM-encrypted TOTP secrets), 5 roles × 14 permissions, admin sessions.
- Append-only moderation actions, live events, append-only config versions (kill switches) and account deletion requests.
- The launch catalogue: 3 coin cosmetics, 2 INR cosmetics and a 100-gem pack.

**Platform API (ADR 0009 has the purchase design):**

| Area          | Endpoints                                                                                                                                                                                   |
| ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Store         | `GET /store/catalog`, `POST/GET /store/orders`, `GET /store/orders/{id}`, `POST /store/orders/{id}/verify`, `POST /store/restore`                                                           |
| Webhooks      | `POST /webhooks/google-play` (Pub/Sub OIDC), `/webhooks/app-store` (ASSN v2 JWS), `/webhooks/dev` (HMAC)                                                                                    |
| Account       | `GET/POST/DELETE /player/deletion`, with a 30-day grace period; signing in still works so the player can cancel                                                                             |
| Public        | `GET /players/{id}/public` (with the last 10 matches), `GET /events`, `GET /config`                                                                                                         |
| Admin         | login with password + TOTP, logout, me, overview, admins, players + sanctions, matches, reports, catalogue workflow, orders + refund, reconcile, audit, live config, events (34 operations) |
| Kill switches | store and matchmaking checked on every order and queue request                                                                                                                              |

- **Admin security:** a TOTP code works once (replay protection), five failures lock the account for 15 minutes, and an unknown email takes as long as a wrong password. Every mutation writes the append-only audit log with the request id.
- **Bootstrap:** `pnpm --filter @rwbr/api admin:create --email … --name … --role SUPER_ADMIN`, with the password in `ADMIN_PASSWORD`, creates the first super-admin and prints the TOTP secret once.
- **Contract:** OpenAPI 0.5.0 covers every new endpoint; types regenerate from it, and every API test response is validated against it.

**Admin portal (Next.js, `apps/admin`):**

- **Pages:** sign-in with MFA, overview tiles, player search and detail with sanctions, report queue, matches, catalogue (create, edit draft, submit, approve, reject, retire, history), orders with refund and reconciliation, audit log with filters and paging, live ops (kill switches with optimistic concurrency, events), admin accounts (create with one-time TOTP display, roles, disable).
- **Session handling:** the session token lives in an httpOnly, SameSite=Strict cookie, and all API calls run on the server, so browser JavaScript never sees it.
- **Permissions:** navigation and buttons follow the admin's permissions; the API enforces the same rules.

**Website (Next.js, `apps/web`):** landing page with live events, news and events, player profiles with recent matches (replacing the React Native companion), support FAQ, account deletion instructions (the web page Google Play requires), privacy policy and terms. The **legal pages are marked as drafts**, with [bracketed] items for counsel.

**Unity:**

- **Store screen:** wallet and catalogue. Coin products buy live; INR products go through `IStorePurchaser`. A dev purchaser is available in the editor and development builds when `RWBR_DEV_PURCHASE_SECRET` is set; otherwise the item shows "(soon)".
- **Account & data screen:** shows the player id, and schedules or cancels deletion with a two-tap confirmation.
- **Purchase flow:** retries network failures with the same idempotency key.

## Tests

| Suite          | Result                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| -------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| API            | 46/46 (was 21). Store: coin purchase atomic and idempotent, dev receipt verify once, forged, wrong-SKU and reused receipts, refund once with shortfall, cosmetic revoke, restore, kill switches, account deletion. Platform: Google verify + acknowledge, outage → 503, RTDN voided (forged token and wrong sender rejected), StoreKit 2 chain checks (untrusted root, missing Apple markers, tampered payload), ASSN refund. Admin: MFA, TOTP replay, lockout, RBAC, two-person publish, sanctions end sessions, reports, refunds, kill switches, events, admin management. RFC 6238 vectors |
| Database       | 10/10 on migrations 0001–0007; the API role still cannot delete sessions or identities (account erasure overwrites and revokes instead)                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| Full stack     | `infrastructure/scripts/fullstack-smoke.sh` passed 17 checks: Phase 3 match loop, then a dev purchase verified twice and granted once, first admin via CLI, TOTP sign-in, audited refund, no ledger drift                                                                                                                                                                                                                                                                                                                                                                                     |
| Unity EditMode | 22/22: dev receipts match the API byte for byte; catalogue DTOs; no real-money purchase without a store                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| Unity PlayMode | 8/8: new store test against a live API (insufficient coins, dev purchase granted, forged receipt refused, deletion scheduled and cancelled), plus all earlier tests                                                                                                                                                                                                                                                                                                                                                                                                                           |
| Browser check  | Admin portal: login with TOTP, overview, reports, catalogue, orders, audit, live ops. Website: profile, unknown profile 404, account deletion, landing                                                                                                                                                                                                                                                                                                                                                                                                                                        |

## Not done in Phase 5 (and why)

- **Real store billing:** Unity IAP, Google Play Billing and StoreKit purchases need developer accounts, product setup in both consoles and signed builds. The API side is ready and tested against mocks; the exit-gate sandbox purchase is still open.
- **Platform sign-in (Google Play Games / Game Center)** is still a 501 stub, so restore across reinstalls only works on the same guest account.
- **Season pass:** not started; moved to Phase 7 ("season pass" in live ops), since it depends on retention data and the economy review.
- **Disabled weapons kill switch:** stored and published, but match servers do not read it yet (Phase 6).
- **In-game player reporting UI and cosmetics on characters:** the APIs exist; the Unity screens do not.
- **Legal sign-off:** privacy policy, terms, data retention period, grievance officer and age rating are drafts for counsel (Phase 6 release gate).
- **Production secrets:** the admin TOTP key, the Google service account and the Apple root need a secrets manager per environment (risk 14).
- **Admin portal hardening:** IP allow-listing or SSO in front of the portal and a CSP are recommended before staging.
