-- Users and their sign-in identities (BRD §9 "Users and identities").
-- A user can have several identities (guest device, Google Play Games, Apple Game Center).

CREATE TABLE users (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  status text NOT NULL DEFAULT 'ACTIVE'
    CHECK (status IN ('ACTIVE', 'SUSPENDED', 'BANNED', 'PENDING_DELETION', 'DELETED')),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  deleted_at timestamptz,
  CHECK ((status = 'DELETED') = (deleted_at IS NOT NULL))
);

CREATE TABLE auth_identities (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  provider text NOT NULL CHECK (provider IN ('GUEST', 'GOOGLE_PLAY', 'APPLE')),
  -- Provider's stable subject ID; for GUEST a server-issued device credential ID (never a raw secret).
  provider_subject text NOT NULL CHECK (length(provider_subject) BETWEEN 1 AND 255),
  created_at timestamptz NOT NULL DEFAULT now(),
  last_used_at timestamptz,
  UNIQUE (provider, provider_subject)
);

CREATE INDEX auth_identities_user_id_idx ON auth_identities (user_id);

CREATE FUNCTION set_updated_at() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at := now();
  RETURN NEW;
END;
$$;

CREATE TRIGGER users_set_updated_at BEFORE UPDATE ON users
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- The app may create and update accounts; deletion is a status change, never a DELETE.
GRANT INSERT, UPDATE ON users TO rwbr_app;
GRANT INSERT, UPDATE ON auth_identities TO rwbr_app;
