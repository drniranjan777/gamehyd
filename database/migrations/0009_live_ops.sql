-- Phase 7: launch and live operations (PHASE-PLAN Phase 7): operations dashboards (API latency, server tick health,
-- capacity and cost), staged releases with automatic rollback thresholds, and the season pass.

-- ---- Operations data ----

-- One row per server per minute, written from heartbeats: capacity (concurrent players), cost (server-minutes) and
-- tick health. Old rows are purged by the reconciliation job.
CREATE TABLE server_load_samples (
  game_server_id uuid NOT NULL REFERENCES game_servers (id) ON DELETE CASCADE,
  minute timestamptz NOT NULL,
  connected_players integer NOT NULL CHECK (connected_players >= 0),
  hosting boolean NOT NULL,
  tick_ms_p99 real CHECK (tick_ms_p99 >= 0),
  PRIMARY KEY (game_server_id, minute)
);
CREATE INDEX server_load_samples_minute_idx ON server_load_samples (minute);

-- API latency per route and minute, as a fixed-bucket histogram (bucket upper bounds live in the API code) so that
-- percentiles can be combined across instances and minutes. One row per API instance, route and minute.
CREATE TABLE api_latency_minutes (
  minute timestamptz NOT NULL,
  instance text NOT NULL CHECK (char_length(instance) BETWEEN 1 AND 64),
  route text NOT NULL CHECK (char_length(route) BETWEEN 1 AND 120),
  requests integer NOT NULL CHECK (requests > 0),
  server_errors integer NOT NULL CHECK (server_errors >= 0),
  buckets integer[] NOT NULL,
  PRIMARY KEY (minute, instance, route)
);

-- ---- Staged releases (store rollouts) ----

CREATE TABLE app_releases (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  platform text NOT NULL CHECK (platform IN ('ANDROID', 'IOS')),
  app_version text NOT NULL CHECK (app_version ~ '^\d+\.\d+\.\d+$'),
  status text NOT NULL DEFAULT 'ROLLING_OUT' CHECK (status IN ('ROLLING_OUT', 'HALTED', 'COMPLETE', 'ROLLED_BACK')),
  rollout_percent integer NOT NULL CHECK (rollout_percent BETWEEN 1 AND 100),
  regions text[] NOT NULL DEFAULT '{}',          -- empty = all launch regions
  -- Rollback threshold: once min_sessions sessions of this version are seen, crash-free sessions below
  -- min_crash_free halt the release (telemetry carries the app version; matches don't, so completion stays global).
  min_crash_free numeric(5, 4) NOT NULL DEFAULT 0.995 CHECK (min_crash_free BETWEEN 0 AND 1),
  min_sessions integer NOT NULL DEFAULT 500 CHECK (min_sessions >= 1),
  halt_reason text CHECK (char_length(halt_reason) <= 300),
  created_by text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (platform, app_version)
);
CREATE TRIGGER app_releases_set_updated_at BEFORE UPDATE ON app_releases FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- ---- Season pass ----

CREATE TABLE seasons (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  key text NOT NULL UNIQUE CHECK (key ~ '^[a-z0-9_-]{3,40}$'),
  title text NOT NULL CHECK (char_length(title) BETWEEN 1 AND 80),
  starts_at timestamptz NOT NULL,
  ends_at timestamptz NOT NULL,
  xp_per_tier integer NOT NULL CHECK (xp_per_tier BETWEEN 100 AND 100000),
  status text NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT', 'PUBLISHED')),
  created_by text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK (ends_at > starts_at)
);
-- Published seasons never overlap, so "the current season" is unambiguous.
CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE seasons ADD CONSTRAINT seasons_no_overlap
  EXCLUDE USING gist (tstzrange(starts_at, ends_at) WITH &&) WHERE (status = 'PUBLISHED');

-- A published season's tiers and dates are frozen, like published products.
CREATE FUNCTION seasons_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'PUBLISHED' AND (NEW.status <> OLD.status OR NEW.starts_at <> OLD.starts_at OR NEW.ends_at <> OLD.ends_at
      OR NEW.xp_per_tier <> OLD.xp_per_tier OR NEW.key <> OLD.key) THEN
    RAISE EXCEPTION 'published season % is immutable', OLD.key;
  END IF;
  RETURN NEW;
END;
$$;
CREATE TRIGGER seasons_guard BEFORE UPDATE ON seasons FOR EACH ROW EXECUTE FUNCTION seasons_guard();

CREATE TABLE season_rewards (
  season_id uuid NOT NULL REFERENCES seasons (id) ON DELETE CASCADE,
  tier integer NOT NULL CHECK (tier BETWEEN 1 AND 100),
  track text NOT NULL CHECK (track IN ('FREE', 'PREMIUM')),
  cosmetic_id uuid REFERENCES cosmetics (id) ON DELETE RESTRICT,
  currency_code text CHECK (currency_code IN ('COIN', 'GEM')),
  amount bigint CHECK (amount > 0),
  CHECK ((cosmetic_id IS NULL) <> (currency_code IS NULL)),
  CHECK ((currency_code IS NULL) = (amount IS NULL)),
  PRIMARY KEY (season_id, tier, track)
);

CREATE FUNCTION season_rewards_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (SELECT status FROM seasons WHERE id = COALESCE(NEW.season_id, OLD.season_id)) = 'PUBLISHED' THEN
    RAISE EXCEPTION 'rewards of a published season are immutable';
  END IF;
  RETURN COALESCE(NEW, OLD);
END;
$$;
CREATE TRIGGER season_rewards_guard BEFORE INSERT OR UPDATE OR DELETE ON season_rewards
  FOR EACH ROW EXECUTE FUNCTION season_rewards_guard();

CREATE TABLE season_progress (
  season_id uuid NOT NULL REFERENCES seasons (id) ON DELETE RESTRICT,
  user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  xp bigint NOT NULL DEFAULT 0 CHECK (xp >= 0),
  premium boolean NOT NULL DEFAULT false,
  premium_order_id uuid REFERENCES orders (id) ON DELETE RESTRICT,
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (season_id, user_id)
);
CREATE TRIGGER season_progress_set_updated_at BEFORE UPDATE ON season_progress FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- One row per reward claimed: the primary key makes every reward pay out exactly once. Append-only.
CREATE TABLE season_claims (
  season_id uuid NOT NULL REFERENCES seasons (id) ON DELETE RESTRICT,
  user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  tier integer NOT NULL,
  track text NOT NULL CHECK (track IN ('FREE', 'PREMIUM')),
  claimed_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (season_id, user_id, tier, track)
);
CREATE TRIGGER season_claims_append_only BEFORE UPDATE ON season_claims
  FOR EACH ROW EXECUTE FUNCTION forbid_update_delete();

-- The premium track is a store product grant; season rewards and the pass credit the ledger with these reasons.
ALTER TABLE store_product_grants DROP CONSTRAINT store_product_grants_grant_type_check;
ALTER TABLE store_product_grants ADD CONSTRAINT store_product_grants_grant_type_check
  CHECK (grant_type IN ('COSMETIC', 'CURRENCY', 'SEASON_PASS'));
ALTER TABLE store_product_grants ADD COLUMN season_id uuid REFERENCES seasons (id) ON DELETE RESTRICT;
ALTER TABLE store_product_grants ADD CONSTRAINT store_product_grants_season_pass
  CHECK ((grant_type = 'SEASON_PASS') = (season_id IS NOT NULL));
ALTER TABLE wallet_transactions DROP CONSTRAINT wallet_transactions_reason_check;
ALTER TABLE wallet_transactions ADD CONSTRAINT wallet_transactions_reason_check
  CHECK (reason IN ('MATCH_REWARD', 'PURCHASE', 'REFUND', 'ADMIN_ADJUSTMENT', 'REVERSAL', 'SEASON_REWARD'));
ALTER TABLE player_inventory DROP CONSTRAINT player_inventory_source_check;
ALTER TABLE player_inventory ADD CONSTRAINT player_inventory_source_check
  CHECK (source IN ('STARTER', 'MATCH_REWARD', 'PURCHASE', 'ADMIN_GRANT', 'SEASON_REWARD'));

INSERT INTO admin_permissions (key, description) VALUES
  ('ops.read', 'Operations dashboards: latency, servers, capacity, cost'),
  ('releases.write', 'Record app releases and their rollback thresholds'),
  ('economy.read', 'Economy reviews: currency sources and sinks');
INSERT INTO admin_role_permissions (role_key, permission_key) VALUES
  ('SUPER_ADMIN', 'ops.read'), ('SUPER_ADMIN', 'releases.write'), ('SUPER_ADMIN', 'economy.read'),
  ('ANALYST', 'ops.read'), ('ANALYST', 'economy.read'), ('ECONOMY_OPERATOR', 'economy.read'),
  ('SUPPORT', 'ops.read');

GRANT INSERT, UPDATE, DELETE ON server_load_samples, api_latency_minutes TO rwbr_app;  -- delete: retention purge
GRANT INSERT, UPDATE ON app_releases, seasons, season_progress TO rwbr_app;
GRANT INSERT, DELETE ON season_rewards TO rwbr_app;                                       -- drafts only (trigger)
GRANT INSERT ON season_claims TO rwbr_app;
