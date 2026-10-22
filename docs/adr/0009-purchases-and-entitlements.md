# ADR 0009 — Purchases, entitlements and refunds

- **Status:** Accepted (22 Oct 2026)
- **Context:** BRD §9 "Store", "Payments", §12 economy guardrails, §14 "zero purchase duplication"; risk 9 (purchase fraud, duplicate grants). Phase 5.

## Decision

1. **The server owns prices and grants.** Clients send a product key and an idempotency key, never a price. Products carry their grants (cosmetics or gems) and per-platform SKUs. Once published, a product's key and price are frozen by a database trigger; a price change is a new product. Publishing needs a second person (`approved_by <> created_by`, enforced in the database and the API).
2. **Two purchase paths.**
   - **Coins and gems:** one transaction locks the wallet, checks ownership, debits through the ledger and grants. The idempotency key makes retries return the same order.
   - **Real money (INR):** the client creates a PENDING order and gets the platform SKU, buys it in Google Play or the App Store, then sends the proof. The server verifies it server to server and fulfils in one transaction. The grant, the payment row and the order status change commit together.
3. **A receipt pays once.** `payment_transactions` is unique on `(platform, provider_transaction_id)` and on `order_id`. A replayed or shared receipt gets 409 `RECEIPT_ALREADY_USED`; a forged or mismatched one gets 422 `RECEIPT_REJECTED`. Purchase tokens are stored only as SHA-256 hashes.
4. **Verification per platform.**
   - **Google Play:** service-account JWT → OAuth token → `purchases.products.get`, then acknowledge after the grant commits. A failed acknowledgement is logged, not fatal: Google refunds unacknowledged purchases after 3 days, which arrives as a voided-purchase notification and revokes the grant.
   - **Apple:** StoreKit 2 signed transactions (JWS). The x5c chain must end at the pinned Apple root, every signature and validity date must check, and the leaf and intermediate must carry Apple's App Store marker extensions. Bundle id and product id must match.
   - **Dev:** HMAC-signed receipts, off unless `DEV_PURCHASES_ENABLED`, so the whole flow runs locally and in CI without store accounts.
5. **Refunds revoke.** Google voided-purchase notifications, Apple REFUND/REVOKE notifications, dev notifications and admin refunds all go through one idempotent `refund()`:
   - remove the purchased cosmetic (and put the starter item back in its slot);
   - claw back granted gems through the ledger, recording any `refund_shortfall` instead of going negative;
   - return coins for in-game purchases.
     Each notification is authenticated (Google OIDC push token, Apple JWS, dev HMAC) and recorded once in append-only `purchase_events`.
6. **Reconciliation.** A job (in-process, `RECONCILE_INTERVAL_SECONDS`, or on demand from the admin portal) cancels stale PENDING orders, reports wallet ledger drift and unmatched platform events, ends expired suspensions and erases accounts whose deletion grace period is over.
7. **Kill switches.** Versioned, append-only `game_config_versions` (store, matchmaking, products off sale, weapons) take effect within 5 seconds on every API instance.

## Consequences

- No purchase can be granted twice: the database rejects it even if application code is wrong. The API tests cover duplicate verification, concurrent replays, duplicate webhooks, forged receipts and refunds with shortfall.
- Google and Apple are tested against a local mock of Google's endpoints and a generated Apple-shaped certificate chain. Real sandbox purchases need the developer accounts, Unity IAP and the release-gate test on devices (Phase 5 exit gate).
- Coins are earned only by playing. The admin API refuses products that sell coins or sell a currency for itself (BRD §12).
- Financial records outlive accounts: orders keep `ON DELETE RESTRICT` on users, and account deletion anonymises the profile instead of deleting the user row.
