# Runbook: payment verification outage

**Symptoms:** players paid but didn't get the item; orders stay PENDING; API errors `503 PLATFORM_UNAVAILABLE` on `/store/orders/{id}/verify`; Google or Apple status pages show an incident.

## Why money is safe

- Grants happen only after server-side verification, in the same transaction as the payment record.
- A purchase token pays for one order only: the database rejects a second use.
- The game retries verification with the same order and proof, so when the platform recovers each purchase completes exactly once.
- If Google never gets an acknowledgement, it refunds the purchase automatically after 3 days. That arrives as a voided-purchase notification and revokes the grant, so the books still balance.

## Contain

1. Confirm it's the platform: API logs show the failing upstream, and the platform status page agrees.
2. To stop new purchases while verification is down, turn the store off: **Live ops → Store open: off**. Coin purchases don't depend on the platforms; prefer switching off only the real-money products by key in **Products off sale**.
3. Post a status event: "Purchases are delayed; nothing is lost; items arrive automatically."

## After recovery

1. Pending purchases complete when the game re-sends the proof (store billing re-delivers unfinished purchases; with Unity IAP this happens on the next launch) or through `POST /store/restore`. Nothing to do per order.
2. **Orders → Run reconciliation now:** stale pending orders (older than `ORDER_PENDING_TTL_HOURS`) are cancelled. These were never paid or verified; a late valid receipt can still be restored.
3. Check **Beta metrics → Purchase integrity** is 0 / 0 / 0, and that unmatched platform events in the reconciliation report are explained.

## Refund storm or chargeback fraud

- Refund notifications revoke items automatically. If many arrive for one product, check the audit log (`order.refund`) and consider taking the product off sale.
- Gems clawed back after being spent show as `refund_shortfall` on the order. Repeated shortfalls on one account are a fraud signal: review and sanction from **Players**.

## Confirm

New purchases fulfil within a minute; pending orders older than 1 hour fall to zero.
