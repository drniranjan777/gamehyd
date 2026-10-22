-- Weapon roster and its versioned balance (BRD §9 "weapons, weapon_balance_versions (append-only)"), and the
-- first real map, hyd-city-v1. The match server's WeaponCatalog (balance v3) is the runtime source; these rows are
-- the auditable record the admin portal will manage (Phase 5). A .NET test checks every value against the catalog.

CREATE TABLE weapons (
  id text PRIMARY KEY CHECK (id ~ '^[a-z0-9_]{3,40}$'),
  catalog_index smallint NOT NULL UNIQUE CHECK (catalog_index BETWEEN 0 AND 254),
  weapon_class text NOT NULL CHECK (weapon_class IN ('PISTOL', 'SMG', 'ASSAULT_RIFLE', 'SHOTGUN', 'MARKSMAN', 'SNIPER')),
  display_name text NOT NULL CHECK (char_length(display_name) BETWEEN 1 AND 40),
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE weapon_balance_versions (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  version integer NOT NULL CHECK (version >= 1),
  weapon_id text NOT NULL REFERENCES weapons (id) ON DELETE RESTRICT,
  balance jsonb NOT NULL CHECK (jsonb_typeof(balance) = 'object' AND (balance ? 'damage') AND (balance ? 'fireIntervalSeconds')),
  published_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (version, weapon_id)
);

CREATE TRIGGER weapon_balance_versions_append_only BEFORE UPDATE OR DELETE ON weapon_balance_versions
  FOR EACH ROW EXECUTE FUNCTION forbid_update_delete();

INSERT INTO weapons (id, catalog_index, weapon_class, display_name) VALUES
  ('sidearm_p9', 0, 'PISTOL', 'P9 Sidearm'),
  ('kestrel_smg', 1, 'SMG', 'Kestrel SMG'),
  ('rifle_standard', 2, 'ASSAULT_RIFLE', 'Standard Rifle'),
  ('pump_12', 3, 'SHOTGUN', 'Pump-12'),
  ('marksman_dmr', 4, 'MARKSMAN', 'Marksman DMR'),
  ('longshot_sr', 5, 'SNIPER', 'Longshot');

INSERT INTO weapon_balance_versions (version, weapon_id, balance) VALUES
  (3, 'sidearm_p9', '{"damage":22,"pellets":1,"headshotMultiplier":1.8,"range":50,"falloffStart":15,"minDamageFactor":0.6,"fireIntervalSeconds":0.22,"magazineSize":12,"reloadSeconds":1.4,"switchSeconds":0.3,"spreadDegrees":0.9,"movingSpreadDegrees":1.0,"airborneSpreadDegrees":3}'),
  (3, 'kestrel_smg', '{"damage":15,"pellets":1,"headshotMultiplier":1.5,"range":60,"falloffStart":12,"minDamageFactor":0.5,"fireIntervalSeconds":0.07,"magazineSize":32,"reloadSeconds":1.8,"switchSeconds":0.4,"spreadDegrees":1.4,"movingSpreadDegrees":0.8,"airborneSpreadDegrees":3}'),
  (3, 'rifle_standard', '{"damage":20,"pellets":1,"headshotMultiplier":1.8,"range":120,"falloffStart":40,"minDamageFactor":0.7,"fireIntervalSeconds":0.12,"magazineSize":30,"reloadSeconds":2.0,"switchSeconds":0.5,"spreadDegrees":0.8,"movingSpreadDegrees":1.5,"airborneSpreadDegrees":4}'),
  (3, 'pump_12', '{"damage":12,"pellets":8,"headshotMultiplier":1.25,"range":30,"falloffStart":6,"minDamageFactor":0.2,"fireIntervalSeconds":0.75,"magazineSize":6,"reloadSeconds":2.6,"switchSeconds":0.5,"spreadDegrees":4.5,"movingSpreadDegrees":0.5,"airborneSpreadDegrees":1.5}'),
  (3, 'marksman_dmr', '{"damage":42,"pellets":1,"headshotMultiplier":2,"range":250,"falloffStart":120,"minDamageFactor":0.8,"fireIntervalSeconds":0.35,"magazineSize":10,"reloadSeconds":2.4,"switchSeconds":0.6,"spreadDegrees":0.25,"movingSpreadDegrees":2.5,"airborneSpreadDegrees":6}'),
  (3, 'longshot_sr', '{"damage":85,"pellets":1,"headshotMultiplier":2.5,"range":400,"falloffStart":300,"minDamageFactor":0.9,"fireIntervalSeconds":1.4,"magazineSize":5,"reloadSeconds":3.0,"switchSeconds":0.8,"spreadDegrees":0.05,"movingSpreadDegrees":4,"airborneSpreadDegrees":8}');

-- hyd-city-v1: generated deterministically by RWBR.Shared.CityMap; the hash pins the exact geometry and loot.
INSERT INTO maps (key, display_name) VALUES ('hyd-city', 'Hyderabad-inspired city');
INSERT INTO map_versions (map_key, version, status, manifest, published_at) VALUES
  ('hyd-city', 'hyd-city-v1', 'PUBLISHED',
   '{"halfSize": 300, "districts": ["Char Gate Plaza", "Old City Bazaar", "Hi-Tech Towers", "Residential Colony", "Lakeside"], "boxes": 3871, "loot": 372, "spawns": 32, "geometrySha256": "767DC0BCF7944070946E4FDC2A6D7C7CFBABCB7A5CC818EC32295409EF7089D3"}',
   now());
