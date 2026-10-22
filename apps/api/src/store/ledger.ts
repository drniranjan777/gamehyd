import { PoolClient } from 'pg';

/**
 * Takes up to `amount` from a wallet through the ledger. With `allowShortfall` (refund clawbacks) it takes what is
 * there and returns how much it took; otherwise it takes all or nothing and returns 0 when funds are short.
 * Idempotent per key, like credit(): a repeated key returns the amount taken the first time.
 */
export async function debit(
  c: PoolClient,
  userId: string,
  currency: 'COIN' | 'GEM',
  amount: number,
  reason: 'PURCHASE' | 'REFUND' | 'ADMIN_ADJUSTMENT',
  reference: string,
  idempotencyKey: string,
  allowShortfall = false,
): Promise<number> {
  await c.query(
    `INSERT INTO wallets (user_id, currency_code) VALUES ($1, $2) ON CONFLICT DO NOTHING`,
    [userId, currency],
  );
  const { rows } = await c.query<{ id: string; balance: string; version: string }>(
    'SELECT id, balance, version FROM wallets WHERE user_id = $1 AND currency_code = $2 FOR UPDATE',
    [userId, currency],
  );
  const w = rows[0]!;
  const seen = await c.query<{ amount: string }>(
    'SELECT amount FROM wallet_transactions WHERE wallet_id = $1 AND idempotency_key = $2',
    [w.id, idempotencyKey],
  );
  if (seen.rowCount) return -Number(seen.rows[0]!.amount);
  const balance = Number(w.balance);
  const take = allowShortfall ? Math.min(balance, amount) : balance >= amount ? amount : 0;
  if (take === 0) return 0;
  const version = Number(w.version) + 1;
  await c.query(
    `INSERT INTO wallet_transactions (wallet_id, amount, balance_after, wallet_version, reason, reference, idempotency_key)
     VALUES ($1, $2, $3, $4, $5, $6, $7)`,
    [w.id, -take, balance - take, version, reason, reference, idempotencyKey],
  );
  await c.query('UPDATE wallets SET balance = $2, version = $3 WHERE id = $1', [
    w.id,
    balance - take,
    version,
  ]);
  return take;
}

/** Locks a wallet row (creating it if needed) so checks made after this are serialised per player and currency. */
export async function lockWallet(
  c: PoolClient,
  userId: string,
  currency: 'COIN' | 'GEM',
): Promise<number> {
  await c.query(
    `INSERT INTO wallets (user_id, currency_code) VALUES ($1, $2) ON CONFLICT DO NOTHING`,
    [userId, currency],
  );
  const { rows } = await c.query<{ balance: string }>(
    'SELECT balance FROM wallets WHERE user_id = $1 AND currency_code = $2 FOR UPDATE',
    [userId, currency],
  );
  return Number(rows[0]!.balance);
}
