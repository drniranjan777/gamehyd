-- Phase 6: client telemetry and the data behind the beta KPIs and release gates (BRD §14, PHASE-PLAN Phase 6 "Track"):
-- crash-free sessions, FPS by device, match completion, disconnects, queue time, D1/D7 retention, payer conversion.

-- Client events, batched by the game. Not append-only: rows are purged after the retention window (privacy).
CREATE TABLE telemetry_events (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id uuid REFERENCES users (id) ON DELETE SET NULL,  -- null before sign-in (e.g. a crash on boot)
  session_id uuid NOT NULL,                               -- one app launch
  event_type text NOT NULL CHECK (event_type IN ('SESSION_START', 'SESSION_END', 'CRASH', 'FPS_SAMPLE', 'DISCONNECT')),
  app_version text NOT NULL CHECK (app_version ~ '^\d+\.\d+\.\d+$'),
  platform text NOT NULL CHECK (platform IN ('ANDROID', 'IOS', 'WINDOWS', 'MACOS', 'LINUX', 'EDITOR')),
  device_model text NOT NULL CHECK (char_length(device_model) BETWEEN 1 AND 100),
  data jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(data) = 'object' AND pg_column_size(data) <= 8192),
  client_time timestamptz NOT NULL,
  received_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX telemetry_events_type_time_idx ON telemetry_events (event_type, received_at);
CREATE INDEX telemetry_events_session_idx ON telemetry_events (session_id);
CREATE INDEX telemetry_events_user_idx ON telemetry_events (user_id, received_at) WHERE user_id IS NOT NULL;

-- Queue time: when the player joined the queue, kept on the participant row (the queue row is deleted later).
ALTER TABLE match_participants ADD COLUMN enqueued_at timestamptz;
ALTER TABLE match_participants ADD CONSTRAINT match_participants_enqueued_before_assigned
  CHECK (enqueued_at IS NULL OR enqueued_at <= assigned_at);

INSERT INTO admin_permissions (key, description) VALUES ('metrics.read', 'Beta KPIs and release gates');
INSERT INTO admin_role_permissions (role_key, permission_key) VALUES
  ('ANALYST', 'metrics.read'), ('SUPER_ADMIN', 'metrics.read'), ('SUPPORT', 'metrics.read');

GRANT INSERT, DELETE ON telemetry_events TO rwbr_app; -- delete: retention purge only
