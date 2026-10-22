-- Phase 5: store and orders, platform payments, admin RBAC with MFA, moderation, live ops and account deletion
-- (BRD §9 "Store", "Payments", "Moderation", "Live operations", "Administration"; §12 economy guardrails; §13).

-- ---- Store ----

CREATE TABLE store_products (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  key text NOT NULL UNIQUE CHECK (key ~ '^[a-z0-9_]{3,60}$'),
  display_name text NOT NULL CHECK (char_length(display_name) BETWEEN 1 AND 60),
  description text NOT NULL DEFAULT '' CHECK (char_length(description) <= 300),
  -- INR: real money through Google Play / App Store billing (minor units = paise). COIN/GEM: in-game currency.
  price_currency text NOT NULL CHECK (price_currency IN ('INR', 'COIN', 'GEM')),
  price_minor bigint NOT NULL CHECK (price_minor > 0),
  status text NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT', 'PENDING_APPROVAL', 'PUBLISHED', 'RETIRED')),
  available_from timestamptz,
  available_until timestamptz,
  created_by text NOT NULL,
  approved_by text,
  published_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  -- Two-person rule (BRD §4 "publishes after approval"): the approver is never the author.
  CHECK (approved_by IS NULL OR approved_by <> created_by),
  CHECK ((status IN ('PUBLISHED', 'RETIRED')) = (approved_by IS NOT NULL)),
  CHECK (available_until IS NULL OR available_from IS NULL OR available_until > available_from)
);

CREATE TRIGGER store_products_set_updated_at BEFORE UPDATE ON store_products FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Lifecycle: DRAFT <-> PENDING_APPROVAL -> PUBLISHED -> RETIRED. Once published, price and identity are frozen
-- (a price change is a new product), so past orders always match what was sold.
CREATE FUNCTION store_products_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NOT ((OLD.status = NEW.status) OR
          (OLD.status = 'DRAFT' AND NEW.status = 'PENDING_APPROVAL') OR
          (OLD.status = 'PENDING_APPROVAL' AND NEW.status IN ('DRAFT', 'PUBLISHED')) OR
          (OLD.status = 'PUBLISHED' AND NEW.status = 'RETIRED')) THEN
    RAISE EXCEPTION 'store product status % -> % not allowed', OLD.status, NEW.status;
  END IF;
  IF OLD.status IN ('PUBLISHED', 'RETIRED') AND (NEW.key <> OLD.key OR NEW.price_currency <> OLD.price_currency OR NEW.price_minor <> OLD.price_minor) THEN
    RAISE EXCEPTION 'published product % is immutable (create a new product instead)', OLD.key;
  END IF;
  IF OLD.status = 'RETIRED' THEN
    RAISE EXCEPTION 'retired product % is immutable', OLD.key;
  END IF;
  RETURN NEW;
END;
$$;
CREATE TRIGGER store_products_guard BEFORE UPDATE ON store_products FOR EACH ROW EXECUTE FUNCTION store_products_guard();

CREATE TABLE store_product_platform_skus (
  product_id uuid NOT NULL REFERENCES store_products (id) ON DELETE CASCADE,
  platform text NOT NULL CHECK (platform IN ('ANDROID', 'IOS')),
  sku text NOT NULL CHECK (sku ~ '^[a-z0-9_.]{3,100}$'),
  PRIMARY KEY (product_id, platform),
  UNIQUE (platform, sku)
);

CREATE TABLE store_product_grants (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  product_id uuid NOT NULL REFERENCES store_products (id) ON DELETE CASCADE,
  grant_type text NOT NULL CHECK (grant_type IN ('COSMETIC', 'CURRENCY')),
  cosmetic_id uuid REFERENCES cosmetics (id) ON DELETE RESTRICT,
  currency_code text CHECK (currency_code IN ('COIN', 'GEM')),
  amount bigint CHECK (amount > 0),
  CHECK ((grant_type = 'COSMETIC') = (cosmetic_id IS NOT NULL)),
  CHECK ((grant_type = 'CURRENCY') = (currency_code IS NOT NULL AND amount IS NOT NULL)),
  UNIQUE (product_id, cosmetic_id)
);

-- ---- Orders and payments ----

CREATE TABLE orders (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users (id) ON DELETE RESTRICT, -- financial records outlive accounts (8 years)
  product_id uuid NOT NULL REFERENCES store_products (id) ON DELETE RESTRICT,
  platform text NOT NULL CHECK (platform IN ('ANDROID', 'IOS', 'IN_GAME', 'DEV')),
  status text NOT NULL DEFAULT 'PENDING'
    CHECK (status IN ('PENDING', 'VERIFIED', 'FULFILLED', 'FAILED', 'CANCELLED', 'REFUNDED')),
  amount_minor bigint NOT NULL CHECK (amount_minor > 0),
  currency_code text NOT NULL CHECK (currency_code IN ('INR', 'COIN', 'GEM')),
  idempotency_key text NOT NULL CHECK (char_length(idempotency_key) BETWEEN 8 AND 100),
  failure_reason text CHECK (char_length(failure_reason) <= 300),
  refund_shortfall bigint NOT NULL DEFAULT 0 CHECK (refund_shortfall >= 0),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  fulfilled_at timestamptz,
  refunded_at timestamptz,
  UNIQUE (user_id, idempotency_key),
  CHECK ((status IN ('FULFILLED', 'REFUNDED')) = (fulfilled_at IS NOT NULL)),
  CHECK ((status = 'REFUNDED') = (refunded_at IS NOT NULL))
);

CREATE INDEX orders_user_idx ON orders (user_id, created_at DESC);
CREATE INDEX orders_status_idx ON orders (status, created_at) WHERE status IN ('PENDING', 'VERIFIED');
CREATE TRIGGER orders_set_updated_at BEFORE UPDATE ON orders FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Database-enforced order state machine (BRD §9). Terminal states never change.
CREATE FUNCTION orders_transition_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = NEW.status THEN
    IF OLD.status IN ('FAILED', 'CANCELLED', 'REFUNDED') THEN
      RAISE EXCEPTION 'order % is % and immutable', OLD.id, OLD.status;
    END IF;
    RETURN NEW;
  END IF;
  IF NOT ((OLD.status = 'PENDING' AND NEW.status IN ('VERIFIED', 'FULFILLED', 'FAILED', 'CANCELLED')) OR
          (OLD.status = 'VERIFIED' AND NEW.status IN ('FULFILLED', 'FAILED')) OR
          (OLD.status = 'FULFILLED' AND NEW.status = 'REFUNDED')) THEN
    RAISE EXCEPTION 'order status % -> % not allowed', OLD.status, NEW.status;
  END IF;
  IF NEW.amount_minor <> OLD.amount_minor OR NEW.currency_code <> OLD.currency_code OR NEW.product_id <> OLD.product_id OR NEW.user_id <> OLD.user_id THEN
    RAISE EXCEPTION 'order % terms are immutable', OLD.id;
  END IF;
  RETURN NEW;
END;
$$;
CREATE TRIGGER orders_transition_guard BEFORE UPDATE ON orders FOR EACH ROW EXECUTE FUNCTION orders_transition_guard();

CREATE TABLE payment_transactions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id uuid NOT NULL UNIQUE REFERENCES orders (id) ON DELETE RESTRICT,
  platform text NOT NULL CHECK (platform IN ('ANDROID', 'IOS', 'DEV')),
  provider_transaction_id text NOT NULL CHECK (char_length(provider_transaction_id) BETWEEN 1 AND 200),
  purchase_token_hash bytea CHECK (octet_length(purchase_token_hash) = 32), -- tokens and receipts are never stored
  environment text NOT NULL CHECK (environment IN ('PRODUCTION', 'SANDBOX', 'DEV')),
  status text NOT NULL DEFAULT 'VERIFIED' CHECK (status IN ('VERIFIED', 'REFUNDED')),
  verified_at timestamptz NOT NULL DEFAULT now(),
  refunded_at timestamptz,
  UNIQUE (platform, provider_transaction_id)
);

-- Every platform notification (Google RTDN, App Store Server Notifications), processed at most once. Append-only.
CREATE TABLE purchase_events (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  platform text NOT NULL CHECK (platform IN ('ANDROID', 'IOS', 'DEV')),
  provider_event_id text NOT NULL CHECK (char_length(provider_event_id) BETWEEN 1 AND 200),
  event_type text NOT NULL CHECK (char_length(event_type) <= 60),
  order_id uuid REFERENCES orders (id) ON DELETE RESTRICT,
  payload_sha256 bytea NOT NULL CHECK (octet_length(payload_sha256) = 32),
  outcome text NOT NULL CHECK (outcome IN ('APPLIED', 'IGNORED', 'UNMATCHED', 'REJECTED')),
  detail text CHECK (char_length(detail) <= 300),
  received_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (platform, provider_event_id)
);
CREATE TRIGGER purchase_events_append_only BEFORE UPDATE OR DELETE ON purchase_events
  FOR EACH ROW EXECUTE FUNCTION forbid_update_delete();

-- ---- Administration (RBAC, MFA) ----

CREATE TABLE admin_users (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  email text NOT NULL UNIQUE CHECK (email = lower(email) AND email ~ '^[^@\s]+@[^@\s]+$'),
  display_name text NOT NULL CHECK (char_length(display_name) BETWEEN 1 AND 60),
  password_hash text NOT NULL CHECK (password_hash LIKE 'scrypt$%'),
  totp_secret_enc bytea NOT NULL, -- AES-256-GCM with the server's admin key; never stored in clear
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'DISABLED')),
  failed_logins integer NOT NULL DEFAULT 0 CHECK (failed_logins >= 0),
  locked_until timestamptz,
  last_totp_step bigint NOT NULL DEFAULT 0, -- replay protection: a TOTP code works once
  last_login_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE admin_roles (key text PRIMARY KEY CHECK (key ~ '^[A-Z_]{3,40}$'), description text NOT NULL);
CREATE TABLE admin_permissions (key text PRIMARY KEY CHECK (key ~ '^[a-z]+\.[a-z]+$'), description text NOT NULL);
CREATE TABLE admin_role_permissions (
  role_key text NOT NULL REFERENCES admin_roles (key) ON DELETE CASCADE,
  permission_key text NOT NULL REFERENCES admin_permissions (key) ON DELETE CASCADE,
  PRIMARY KEY (role_key, permission_key)
);
CREATE TABLE admin_user_roles (
  admin_user_id uuid NOT NULL REFERENCES admin_users (id) ON DELETE CASCADE,
  role_key text NOT NULL REFERENCES admin_roles (key) ON DELETE RESTRICT,
  granted_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (admin_user_id, role_key)
);
CREATE TABLE admin_sessions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  admin_user_id uuid NOT NULL REFERENCES admin_users (id) ON DELETE CASCADE,
  token_hash bytea NOT NULL UNIQUE CHECK (octet_length(token_hash) = 32),
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  revoked_at timestamptz,
  user_agent text CHECK (char_length(user_agent) <= 200)
);
CREATE INDEX admin_sessions_user_idx ON admin_sessions (admin_user_id);

INSERT INTO admin_roles (key, description) VALUES
  ('SUPPORT', 'Player and order lookups for support tickets'),
  ('MODERATOR', 'Reports and sanctions'),
  ('ECONOMY_OPERATOR', 'Catalogue, approvals, orders, refunds'),
  ('ANALYST', 'Read-only operational data'),
  ('SUPER_ADMIN', 'Everything, including admin accounts and live ops');
INSERT INTO admin_permissions (key, description) VALUES
  ('players.read', 'Search and view players'),
  ('players.sanction', 'Warn, suspend, ban, unban'),
  ('matches.read', 'View matches and result evidence'),
  ('catalog.read', 'View store products'),
  ('catalog.write', 'Create and edit draft products'),
  ('catalog.approve', 'Approve and publish products (not your own)'),
  ('orders.read', 'View orders and payments'),
  ('orders.reconcile', 'Re-verify pending orders with the platform'),
  ('orders.refund', 'Refund orders and revoke grants'),
  ('reports.read', 'View player reports'),
  ('reports.resolve', 'Change report status'),
  ('audit.read', 'Read the audit trail'),
  ('liveops.write', 'Live events and kill switches'),
  ('admins.manage', 'Create admins and assign roles');
INSERT INTO admin_role_permissions (role_key, permission_key) VALUES
  ('SUPPORT', 'players.read'), ('SUPPORT', 'matches.read'), ('SUPPORT', 'orders.read'), ('SUPPORT', 'reports.read'),
  ('MODERATOR', 'players.read'), ('MODERATOR', 'players.sanction'), ('MODERATOR', 'matches.read'),
  ('MODERATOR', 'reports.read'), ('MODERATOR', 'reports.resolve'),
  ('ECONOMY_OPERATOR', 'catalog.read'), ('ECONOMY_OPERATOR', 'catalog.write'), ('ECONOMY_OPERATOR', 'catalog.approve'),
  ('ECONOMY_OPERATOR', 'orders.read'), ('ECONOMY_OPERATOR', 'orders.reconcile'), ('ECONOMY_OPERATOR', 'orders.refund'),
  ('ANALYST', 'players.read'), ('ANALYST', 'matches.read'), ('ANALYST', 'orders.read'), ('ANALYST', 'catalog.read'),
  ('ANALYST', 'audit.read');
INSERT INTO admin_role_permissions (role_key, permission_key) SELECT 'SUPER_ADMIN', key FROM admin_permissions;

-- ---- Moderation ----

CREATE TABLE moderation_actions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  action text NOT NULL CHECK (action IN ('WARN', 'SUSPEND', 'BAN', 'UNBAN')),
  reason text NOT NULL CHECK (char_length(reason) BETWEEN 3 AND 500),
  report_id uuid REFERENCES player_reports (id) ON DELETE SET NULL,
  expires_at timestamptz,
  admin_user_id uuid NOT NULL REFERENCES admin_users (id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((action = 'SUSPEND') = (expires_at IS NOT NULL))
);
CREATE INDEX moderation_actions_user_idx ON moderation_actions (user_id, created_at DESC);
CREATE TRIGGER moderation_actions_append_only BEFORE UPDATE OR DELETE ON moderation_actions
  FOR EACH ROW EXECUTE FUNCTION forbid_update_delete();

-- ---- Live operations ----

CREATE TABLE live_events (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  key text NOT NULL UNIQUE CHECK (key ~ '^[a-z0-9_-]{3,60}$'),
  title text NOT NULL CHECK (char_length(title) BETWEEN 1 AND 80),
  body text NOT NULL DEFAULT '' CHECK (char_length(body) <= 2000),
  starts_at timestamptz NOT NULL,
  ends_at timestamptz NOT NULL,
  status text NOT NULL DEFAULT 'SCHEDULED' CHECK (status IN ('SCHEDULED', 'CANCELLED')),
  created_by text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK (ends_at > starts_at)
);

-- Versioned kill switches and live settings; the highest version is in force. Append-only history.
CREATE TABLE game_config_versions (
  version integer PRIMARY KEY CHECK (version >= 1),
  config jsonb NOT NULL CHECK (jsonb_typeof(config) = 'object'),
  reason text NOT NULL CHECK (char_length(reason) BETWEEN 3 AND 300),
  created_by text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TRIGGER game_config_versions_append_only BEFORE UPDATE OR DELETE ON game_config_versions
  FOR EACH ROW EXECUTE FUNCTION forbid_update_delete();
INSERT INTO game_config_versions (version, config, reason, created_by) VALUES
  (1, '{"storeEnabled": true, "matchmakingEnabled": true, "disabledProducts": [], "disabledWeapons": []}', 'initial settings', 'system');

-- ---- Account deletion (BRD §13 privacy) ----

CREATE TABLE account_deletion_requests (
  user_id uuid PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
  requested_at timestamptz NOT NULL DEFAULT now(),
  scheduled_for timestamptz NOT NULL,
  cancelled_at timestamptz,
  completed_at timestamptz,
  CHECK (scheduled_for > requested_at)
);

-- ---- Reference data: launch catalogue ----

INSERT INTO cosmetics (key, kind, display_name, rarity, attributes) VALUES
  ('skin_rifle_neon_01', 'WEAPON_SKIN', 'Neon Bazaar', 'RARE', '{"color": "#ff3df2"}'),
  ('skin_rifle_charminar_gold', 'WEAPON_SKIN', 'Char Gate Gold', 'EPIC', '{"color": "#d4a537"}'),
  ('outfit_lakeside_runner', 'OUTFIT', 'Lakeside Runner', 'RARE', '{"color": "#2f8f83"}'),
  ('emote_victory_wave', 'EMOTE', 'Victory Wave', 'COMMON', '{}'),
  ('banner_irani_chai', 'BANNER', 'Irani Chai', 'COMMON', '{}');

WITH p AS (
  INSERT INTO store_products (key, display_name, description, price_currency, price_minor, status, created_by, approved_by, published_at) VALUES
    ('skin_rifle_neon_01', 'Neon Bazaar rifle skin', 'Glow-in-the-dark finish. Cosmetic only.', 'COIN', 400, 'PUBLISHED', 'system', 'system-review', now()),
    ('banner_irani_chai', 'Irani Chai banner', 'Profile banner. Cosmetic only.', 'COIN', 150, 'PUBLISHED', 'system', 'system-review', now()),
    ('emote_victory_wave', 'Victory Wave emote', 'Celebrate the win.', 'COIN', 250, 'PUBLISHED', 'system', 'system-review', now()),
    ('skin_rifle_charminar_gold', 'Char Gate Gold rifle skin', 'Premium finish. Cosmetic only.', 'INR', 7900, 'PUBLISHED', 'system', 'system-review', now()),
    ('outfit_lakeside_runner', 'Lakeside Runner outfit', 'Outfit. Cosmetic only.', 'INR', 14900, 'PUBLISHED', 'system', 'system-review', now()),
    ('gems_100', '100 Gems', 'Premium currency for future cosmetics.', 'INR', 8900, 'PUBLISHED', 'system', 'system-review', now())
  RETURNING id, key
)
INSERT INTO store_product_grants (product_id, grant_type, cosmetic_id, currency_code, amount)
SELECT p.id, 'COSMETIC', c.id, NULL, NULL FROM p JOIN cosmetics c ON c.key = p.key
UNION ALL
SELECT p.id, 'CURRENCY', NULL, 'GEM', 100 FROM p WHERE p.key = 'gems_100';

INSERT INTO store_product_platform_skus (product_id, platform, sku)
SELECT id, platform, 'rwbr.' || key FROM store_products, (VALUES ('ANDROID'), ('IOS')) v(platform) WHERE price_currency = 'INR';

-- ---- Grants for the API role ----

GRANT INSERT, UPDATE ON store_products, orders, payment_transactions, admin_users, admin_sessions, live_events,
  account_deletion_requests TO rwbr_app;
GRANT INSERT, DELETE ON store_product_platform_skus, store_product_grants, admin_user_roles TO rwbr_app;
GRANT INSERT ON purchase_events, moderation_actions, game_config_versions TO rwbr_app;
GRANT DELETE ON player_inventory TO rwbr_app; -- refunds revoke the cosmetic they paid for
GRANT UPDATE ON player_reports TO rwbr_app;   -- moderators resolve reports
GRANT DELETE ON loadout_slots TO rwbr_app;     -- account deletion clears equipped items

-- Account deletion revokes sessions with their own reason (the app role still cannot delete sessions or identities).
ALTER TABLE player_sessions DROP CONSTRAINT player_sessions_revoke_reason_check;
ALTER TABLE player_sessions ADD CONSTRAINT player_sessions_revoke_reason_check
  CHECK (revoke_reason IN ('ROTATED', 'LOGOUT', 'REUSE_DETECTED', 'ADMIN', 'ACCOUNT_DELETED'));
