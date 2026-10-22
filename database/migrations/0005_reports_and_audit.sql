-- Player reports and the append-only audit log (BRD §5.5 reports, §9 "Moderation", "Administration").
-- Admin users, roles and permissions arrive with the admin portal (Phase 5); the audit log is needed now
-- for security events such as refresh-token reuse.

CREATE TABLE player_reports (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  reporter_user_id uuid REFERENCES users (id) ON DELETE SET NULL,
  reported_user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  match_id uuid REFERENCES matches (id) ON DELETE SET NULL,
  reason text NOT NULL CHECK (reason IN ('CHEATING', 'HARASSMENT', 'INAPPROPRIATE_NAME', 'GRIEFING', 'OTHER')),
  details text CHECK (char_length(details) <= 1000),
  status text NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN', 'UNDER_REVIEW', 'ACTIONED', 'DISMISSED')),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK (reporter_user_id IS DISTINCT FROM reported_user_id)
);

CREATE INDEX player_reports_reported_idx ON player_reports (reported_user_id, created_at DESC);
CREATE INDEX player_reports_queue_idx ON player_reports (status, created_at) WHERE status IN ('OPEN', 'UNDER_REVIEW');
CREATE TRIGGER player_reports_set_updated_at BEFORE UPDATE ON player_reports FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE TABLE admin_audit_logs (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  actor text NOT NULL CHECK (char_length(actor) BETWEEN 1 AND 100),  -- 'system', 'admin:<id>', 'server:<id>'
  action text NOT NULL CHECK (char_length(action) BETWEEN 1 AND 100),
  entity_type text NOT NULL CHECK (char_length(entity_type) BETWEEN 1 AND 50),
  entity_id text NOT NULL CHECK (char_length(entity_id) BETWEEN 1 AND 100),
  reason text CHECK (char_length(reason) <= 500),
  details jsonb CHECK (details IS NULL OR jsonb_typeof(details) = 'object'),
  request_id text CHECK (char_length(request_id) <= 64),
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX admin_audit_logs_entity_idx ON admin_audit_logs (entity_type, entity_id, created_at DESC);
CREATE TRIGGER admin_audit_logs_append_only BEFORE UPDATE OR DELETE ON admin_audit_logs
  FOR EACH ROW EXECUTE FUNCTION forbid_update_delete();
CREATE TRIGGER admin_audit_logs_no_truncate BEFORE TRUNCATE ON admin_audit_logs
  FOR EACH STATEMENT EXECUTE FUNCTION forbid_update_delete();

GRANT INSERT ON player_reports TO rwbr_app;
GRANT INSERT ON admin_audit_logs TO rwbr_app;
