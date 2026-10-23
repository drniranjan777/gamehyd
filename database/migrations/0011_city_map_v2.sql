-- hyd-city-v2 (Phase C part 3): the 900 m city. hyd-city-v1 unchanged at the centre plus a ring of four districts:
-- the Industrial Estate (warehouses, container yards, a factory), Musi Riverside (a river with three road bridges
-- and farmland beyond), the Outskirts (open fields and farmhouses) and the Logistics Park (container stacks,
-- warehouses, a truck depot). Generated deterministically by RWBR.Shared.CityMap.BuildV2; the hash pins the exact
-- geometry and loot spots (a .NET test checks it). What each loot spot holds comes from the live-ops loot table.
-- Matchmaking keeps using DEFAULT_MAP_VERSION (hyd-city-v1) until it is switched after the match servers that know
-- hyd-city-v2 are deployed.
INSERT INTO map_versions (map_key, version, status, manifest, published_at) VALUES
  ('hyd-city', 'hyd-city-v2', 'PUBLISHED',
   '{"halfSize": 450, "districts": ["Char Gate Plaza", "Old City Bazaar", "Hi-Tech Towers", "Residential Colony", "Lakeside", "Industrial Estate", "Outskirts", "Musi Riverside", "Logistics Park"], "boxes": 4522, "loot": 443, "spawns": 40, "geometrySha256": "A21380AD94FE8E45BDACE09661E38C770303A984E85DA3C271E86522E313D2D3"}',
   now());
