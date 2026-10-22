-- Wallets with an append-only ledger, cosmetics catalogue, inventory and loadouts
-- (BRD §9 "Wallets", "Catalog", "Inventory and loadouts", §12 economy guardrails).

CREATE TABLE wallets (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  currency_code text NOT NULL CHECK (currency_code IN ('COIN', 'GEM')),
  balance bigint NOT NULL DEFAULT 0 CHECK (balance >= 0),
  version bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (user_id, currency_code)
);

CREATE TRIGGER wallets_set_updated_at BEFORE UPDATE ON wallets FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- The balance is a projection of this ledger: exactly one row per wallet version, written in the same
-- transaction that bumps the wallet. Corrections are compensating rows, never edits.
CREATE TABLE wallet_transactions (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  public_id uuid NOT NULL UNIQUE DEFAULT gen_random_uuid(),
  wallet_id uuid NOT NULL REFERENCES wallets (id) ON DELETE RESTRICT,
  amount bigint NOT NULL CHECK (amount <> 0),
  balance_after bigint NOT NULL CHECK (balance_after >= 0),
  wallet_version bigint NOT NULL CHECK (wallet_version >= 1),
  reason text NOT NULL CHECK (reason IN ('MATCH_REWARD', 'PURCHASE', 'REFUND', 'ADMIN_ADJUSTMENT', 'REVERSAL')),
  reference text NOT NULL CHECK (char_length(reference) BETWEEN 1 AND 200),
  idempotency_key text NOT NULL CHECK (char_length(idempotency_key) BETWEEN 8 AND 200),
  reverses_transaction_id bigint REFERENCES wallet_transactions (id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (wallet_id, idempotency_key),
  UNIQUE (wallet_id, wallet_version)
);

CREATE TRIGGER wallet_transactions_append_only BEFORE UPDATE OR DELETE ON wallet_transactions
  FOR EACH ROW EXECUTE FUNCTION forbid_update_delete();
CREATE TRIGGER wallet_transactions_no_truncate BEFORE TRUNCATE ON wallet_transactions
  FOR EACH STATEMENT EXECUTE FUNCTION forbid_update_delete();

-- Reconciliation: any wallet whose balance differs from its ledger sum (should always be empty).
CREATE VIEW wallet_ledger_drift AS
  SELECT w.id AS wallet_id, w.balance, COALESCE(SUM(t.amount), 0) AS ledger_sum
  FROM wallets w LEFT JOIN wallet_transactions t ON t.wallet_id = w.id
  GROUP BY w.id, w.balance
  HAVING w.balance <> COALESCE(SUM(t.amount), 0);

CREATE TABLE cosmetics (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  key text NOT NULL UNIQUE CHECK (key ~ '^[a-z0-9_]{3,60}$'),
  kind text NOT NULL CHECK (kind IN ('WEAPON_SKIN', 'OUTFIT', 'EMOTE', 'PARACHUTE', 'BANNER')),
  display_name text NOT NULL CHECK (char_length(display_name) BETWEEN 1 AND 60),
  rarity text NOT NULL CHECK (rarity IN ('COMMON', 'RARE', 'EPIC', 'LEGENDARY')),
  attributes jsonb NOT NULL DEFAULT '{}'
    -- No paid gameplay advantage (BRD §1, §12): cosmetics can never carry competitive stats.
    CHECK (jsonb_typeof(attributes) = 'object' AND NOT (attributes ?| ARRAY[
      'damage', 'fire_rate', 'fireRate', 'recoil', 'spread', 'range', 'magazine', 'reload',
      'health', 'armor', 'speed', 'accuracy', 'hitbox'])),
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE player_inventory (
  user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  cosmetic_id uuid NOT NULL REFERENCES cosmetics (id) ON DELETE RESTRICT,
  source text NOT NULL CHECK (source IN ('STARTER', 'MATCH_REWARD', 'PURCHASE', 'ADMIN_GRANT')),
  source_reference text NOT NULL CHECK (char_length(source_reference) BETWEEN 1 AND 200),
  acquired_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, cosmetic_id)
);

-- Equipped items. The composite foreign key means a player can only equip what they own.
CREATE TABLE loadout_slots (
  user_id uuid NOT NULL,
  slot text NOT NULL CHECK (slot IN ('WEAPON_SKIN', 'OUTFIT', 'EMOTE', 'PARACHUTE', 'BANNER')),
  cosmetic_id uuid NOT NULL,
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, slot),
  FOREIGN KEY (user_id, cosmetic_id) REFERENCES player_inventory (user_id, cosmetic_id) ON DELETE CASCADE
);

CREATE FUNCTION loadout_slot_matches_kind() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (SELECT kind FROM cosmetics WHERE id = NEW.cosmetic_id) <> NEW.slot THEN
    RAISE EXCEPTION 'cosmetic % does not fit slot %', NEW.cosmetic_id, NEW.slot;
  END IF;
  NEW.updated_at := now();
  RETURN NEW;
END;
$$;
CREATE TRIGGER loadout_slots_kind BEFORE INSERT OR UPDATE ON loadout_slots
  FOR EACH ROW EXECUTE FUNCTION loadout_slot_matches_kind();

-- Reference data: starter cosmetics every new player receives.
INSERT INTO cosmetics (key, kind, display_name, rarity, attributes) VALUES
  ('skin_rifle_default', 'WEAPON_SKIN', 'Standard issue', 'COMMON', '{"color": "#5a5a5a"}'),
  ('outfit_default', 'OUTFIT', 'Street wear', 'COMMON', '{"color": "#3366cc"}'),
  ('banner_default', 'BANNER', 'Charminar sunrise', 'COMMON', '{}');

GRANT INSERT, UPDATE ON wallets TO rwbr_app;
GRANT INSERT ON wallet_transactions TO rwbr_app;
GRANT INSERT ON player_inventory TO rwbr_app;
GRANT INSERT, UPDATE ON loadout_slots TO rwbr_app;
GRANT SELECT ON wallet_ledger_drift TO rwbr_app;
