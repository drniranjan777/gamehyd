-- Maps, game servers, matches, participants, matchmaking queue and result verification
-- (BRD §9 "Maps", "Matches", "Servers and verification"). Real-time state never lands here.

CREATE TABLE maps (
  key text PRIMARY KEY CHECK (key ~ '^[a-z0-9-]{3,40}$'),
  display_name text NOT NULL CHECK (char_length(display_name) BETWEEN 1 AND 60),
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE map_versions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  map_key text NOT NULL REFERENCES maps (key) ON DELETE RESTRICT,
  version text NOT NULL UNIQUE CHECK (version ~ '^[a-z0-9.-]{3,60}$'),
  status text NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT', 'PUBLISHED', 'RETIRED')),
  manifest jsonb NOT NULL CHECK (jsonb_typeof(manifest) = 'object'),
  published_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((status = 'DRAFT') = (published_at IS NULL))
);

-- A published map version is immutable (BRD 5.4): only DRAFT -> PUBLISHED -> RETIRED, content frozen.
CREATE FUNCTION map_versions_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status <> 'DRAFT' AND (NEW.manifest IS DISTINCT FROM OLD.manifest OR NEW.version <> OLD.version OR NEW.map_key <> OLD.map_key) THEN
    RAISE EXCEPTION 'map version % is % and immutable', OLD.version, OLD.status;
  END IF;
  IF NOT ((OLD.status = NEW.status) OR (OLD.status = 'DRAFT' AND NEW.status = 'PUBLISHED') OR (OLD.status = 'PUBLISHED' AND NEW.status = 'RETIRED')) THEN
    RAISE EXCEPTION 'map version status % -> % not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END;
$$;
CREATE TRIGGER map_versions_guard BEFORE UPDATE ON map_versions FOR EACH ROW EXECUTE FUNCTION map_versions_guard();

CREATE TABLE game_servers (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  address text NOT NULL UNIQUE CHECK (address ~ '^[A-Za-z0-9.:-]{3,100}:[0-9]{1,5}$'),
  region text NOT NULL CHECK (region ~ '^[A-Z]{2}-[A-Z]{2,10}$'),
  build_version text NOT NULL CHECK (char_length(build_version) <= 40),
  status text NOT NULL DEFAULT 'AVAILABLE' CHECK (status IN ('AVAILABLE', 'DRAINING', 'OFFLINE')),
  -- What the server said in its last heartbeat: IDLE (can take a match) or HOSTING one.
  reported_state text NOT NULL DEFAULT 'IDLE' CHECK (reported_state IN ('IDLE', 'HOSTING')),
  map_versions text[] NOT NULL DEFAULT '{}',
  connected_players integer NOT NULL DEFAULT 0 CHECK (connected_players >= 0),
  last_heartbeat_at timestamptz NOT NULL DEFAULT now(),
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE matches (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  game_server_id uuid NOT NULL REFERENCES game_servers (id) ON DELETE RESTRICT,
  map_version text NOT NULL REFERENCES map_versions (version) ON DELETE RESTRICT,
  mode text NOT NULL CHECK (mode IN ('SOLO')),
  region text NOT NULL,
  capacity integer NOT NULL CHECK (capacity BETWEEN 2 AND 100),
  ruleset_version text NOT NULL,
  status text NOT NULL DEFAULT 'FORMING'
    CHECK (status IN ('FORMING', 'ACTIVE', 'COMPLETED', 'CANCELLED', 'TERMINATED')),
  completion_reason text CHECK (completion_reason IN ('WINNER', 'TIMEOUT', 'CANCELLED')),
  winner_user_id uuid REFERENCES users (id) ON DELETE SET NULL,
  duration_seconds integer CHECK (duration_seconds >= 0),
  result_sha256 bytea CHECK (octet_length(result_sha256) = 32),
  created_at timestamptz NOT NULL DEFAULT now(),
  -- When the server confirmed it opened this match; seats are only handed out after this.
  hosted_at timestamptz,
  started_at timestamptz,
  ended_at timestamptz,
  CHECK ((status = 'COMPLETED') = (result_sha256 IS NOT NULL))
);

CREATE INDEX matches_server_status_idx ON matches (game_server_id, status);
CREATE INDEX matches_forming_idx ON matches (region, mode, created_at) WHERE status = 'FORMING';

-- Database-enforced match lifecycle (BRD 5.1); terminal states never change.
CREATE FUNCTION matches_transition_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = NEW.status THEN
    IF OLD.status IN ('COMPLETED', 'CANCELLED', 'TERMINATED') THEN
      RAISE EXCEPTION 'match % is % and immutable', OLD.id, OLD.status;
    END IF;
    RETURN NEW;
  END IF;
  IF NOT (
    (OLD.status = 'FORMING' AND NEW.status IN ('ACTIVE', 'COMPLETED', 'CANCELLED')) OR
    (OLD.status = 'ACTIVE' AND NEW.status IN ('COMPLETED', 'CANCELLED', 'TERMINATED'))
  ) THEN
    RAISE EXCEPTION 'match status % -> % not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END;
$$;
CREATE TRIGGER matches_transition_guard BEFORE UPDATE ON matches FOR EACH ROW EXECUTE FUNCTION matches_transition_guard();

CREATE TABLE match_participants (
  match_id uuid NOT NULL REFERENCES matches (id) ON DELETE CASCADE,
  user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  assigned_at timestamptz NOT NULL DEFAULT now(),
  placement integer CHECK (placement >= 1),
  kills integer CHECK (kills >= 0),
  survival_seconds integer CHECK (survival_seconds >= 0),
  xp_awarded integer CHECK (xp_awarded >= 0),
  coins_awarded integer CHECK (coins_awarded >= 0),
  completed_at timestamptz,
  PRIMARY KEY (match_id, user_id),
  UNIQUE (match_id, placement)
);

-- Match history: newest first per player, cursor-paginated on (completed_at, match_id).
CREATE INDEX match_participants_history_idx ON match_participants (user_id, completed_at DESC, match_id DESC)
  WHERE completed_at IS NOT NULL;

CREATE TABLE matchmaking_queue (
  user_id uuid PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
  mode text NOT NULL CHECK (mode IN ('SOLO')),
  region text NOT NULL,
  client_version text NOT NULL CHECK (char_length(client_version) <= 20),
  enqueued_at timestamptz NOT NULL DEFAULT now(),
  match_id uuid REFERENCES matches (id) ON DELETE CASCADE,
  matched_at timestamptz,
  CHECK ((match_id IS NULL) = (matched_at IS NULL))
);

CREATE INDEX matchmaking_queue_match_idx ON matchmaking_queue (match_id);

-- Every result submission is recorded, accepted or not (BRD §14 forged-result evidence). Append-only.
CREATE TABLE match_verification_records (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  match_id uuid,
  game_server_id uuid,
  payload_sha256 bytea NOT NULL CHECK (octet_length(payload_sha256) = 32),
  signature_valid boolean NOT NULL,
  outcome text NOT NULL CHECK (outcome IN ('ACCEPTED', 'DUPLICATE', 'REJECTED')),
  reason text CHECK (char_length(reason) <= 300),
  received_at timestamptz NOT NULL DEFAULT now()
);

CREATE FUNCTION forbid_update_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
END;
$$;
CREATE TRIGGER match_verification_records_append_only BEFORE UPDATE OR DELETE ON match_verification_records
  FOR EACH ROW EXECUTE FUNCTION forbid_update_delete();
CREATE TRIGGER match_verification_records_no_truncate BEFORE TRUNCATE ON match_verification_records
  FOR EACH STATEMENT EXECUTE FUNCTION forbid_update_delete();

-- Reference data: the greybox map versions the match server ships with.
INSERT INTO maps (key, display_name) VALUES ('greybox-block', 'Greybox city block');
INSERT INTO map_versions (map_key, version, status, manifest, published_at) VALUES
  ('greybox-block', 'greybox-block-100-v2', 'PUBLISHED', '{"halfSize": 100, "loot": 12, "assets": []}', now()),
  ('greybox-block', 'greybox-block-40-v2', 'PUBLISHED', '{"halfSize": 40, "loot": 12, "assets": [], "purpose": "test"}', now());

GRANT INSERT, UPDATE ON game_servers, matches, match_participants TO rwbr_app;
GRANT INSERT, UPDATE, DELETE ON matchmaking_queue TO rwbr_app;
GRANT DELETE ON match_participants TO rwbr_app; -- leaving a FORMING match before it starts
GRANT INSERT ON match_verification_records TO rwbr_app;
