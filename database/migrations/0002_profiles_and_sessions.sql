-- Player profiles and refresh-token sessions (BRD §9 "Player profiles and sessions").

-- Guest sign-in: the device holds a random secret; only its SHA-256 is stored.
ALTER TABLE auth_identities ADD COLUMN credential_hash bytea;
ALTER TABLE auth_identities ADD CONSTRAINT auth_identities_guest_credential
  CHECK ((provider = 'GUEST') = (credential_hash IS NOT NULL));

CREATE TABLE player_profiles (
  user_id uuid PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
  display_name text NOT NULL
    CHECK (char_length(display_name) BETWEEN 3 AND 24 AND display_name ~ '^[A-Za-z0-9_. -]+$'),
  level integer NOT NULL DEFAULT 1 CHECK (level >= 1),
  xp bigint NOT NULL DEFAULT 0 CHECK (xp >= 0),
  matches_played integer NOT NULL DEFAULT 0 CHECK (matches_played >= 0),
  wins integer NOT NULL DEFAULT 0 CHECK (wins >= 0 AND wins <= matches_played),
  kills integer NOT NULL DEFAULT 0 CHECK (kills >= 0),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TRIGGER player_profiles_set_updated_at BEFORE UPDATE ON player_profiles
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Refresh tokens rotate on every use. All tokens from one sign-in share a family; presenting a token that was
-- already rotated revokes the whole family (stolen-token detection).
CREATE TABLE player_sessions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  family_id uuid NOT NULL,
  refresh_token_hash bytea NOT NULL UNIQUE CHECK (octet_length(refresh_token_hash) = 32),
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  rotated_at timestamptz,
  revoked_at timestamptz,
  revoke_reason text CHECK (revoke_reason IN ('ROTATED', 'LOGOUT', 'REUSE_DETECTED', 'ADMIN')),
  user_agent text CHECK (char_length(user_agent) <= 200),
  CHECK ((revoked_at IS NULL) = (revoke_reason IS NULL)),
  CHECK (expires_at > created_at)
);

CREATE INDEX player_sessions_user_idx ON player_sessions (user_id);
CREATE INDEX player_sessions_family_idx ON player_sessions (family_id);

GRANT INSERT, UPDATE ON player_profiles TO rwbr_app;
GRANT INSERT, UPDATE ON player_sessions TO rwbr_app;
