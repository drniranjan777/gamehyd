#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace RWBR.Shared
{
    /// <summary>
    /// "hyd-city-v1": a fictional 600 m city inspired by Hyderabad's street life (BRD 5.4: recognisable feel, not a
    /// reconstruction of real places or private buildings). Generated deterministically from a fixed seed with our
    /// own PRNG, so the match server and every client build identical geometry from code; no map file to ship or
    /// keep in sync. A published version never changes: changes ship as hyd-city-v2.
    ///
    /// Districts: Old City bazaar (NW), Hi-Tech towers (NE), Residential colony (SW), Lakeside (SE), and the
    /// Char Gate plaza at the centre, with a metro viaduct along x = 150.
    ///
    /// "hyd-city-v2" (900 m) is v1 unchanged at the centre plus a 150 m ring: the Industrial Estate (north: warehouses
    /// you can enter, container yards, a factory chimney), Musi Riverside (east: a river with three road bridges and
    /// farmland beyond), the Outskirts (south: open fields, rocks, trees and farmhouses) and the Logistics Park (west:
    /// container stacks, warehouses, a truck depot). Built by the same generator after the v1 districts, so v1's random
    /// sequence, and so its pinned geometry, is untouched.
    /// </summary>
    public static class CityMap
    {
        public const string Version = "hyd-city-v1";
        public const string VersionV2 = "hyd-city-v2";
        public const float HalfSize = 300f;
        public const float HalfSizeV2 = 450f;
        /// <summary>The river's banks (x) in v2; bridges cross it at <see cref="BridgeZs"/>.</summary>
        public const float RiverWest = 340f, RiverEast = 368f;
        public static readonly float[] BridgeZs = { -220f, 0f, 220f };
        private const float BridgeHalfWidth = 7f;
        private const uint Seed = 0x48594431; // "HYD1"
        private const float AvenueHalfWidth = 10f;

        // Built once, even when several threads ask at the same time (the server, tests running in parallel).
        private static readonly System.Lazy<MapLayout> Cached = new System.Lazy<MapLayout>(() => Generate(v2: false));
        private static readonly System.Lazy<MapLayout> CachedV2 = new System.Lazy<MapLayout>(() => Generate(v2: true));

        /// <summary>The map is immutable, so one shared instance is enough (queries are thread-safe).</summary>
        public static MapLayout Build() => Cached.Value;

        /// <summary>hyd-city-v2 (see the class summary).</summary>
        public static MapLayout BuildV2() => CachedV2.Value;

        private sealed class Builder
        {
            public readonly List<Aabb> Boxes = new List<Aabb>();
            public readonly List<BoxKind> Kinds = new List<BoxKind>();
            public readonly List<LootSpawn> Loot = new List<LootSpawn>();
            public readonly List<Vector3> Spawns = new List<Vector3>();
            private uint _state = Seed;

            public void Box(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, BoxKind kind)
            {
                Boxes.Add(new Aabb(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ)));
                Kinds.Add(kind);
            }

            public void Column(float x, float z, float sx, float sz, float height, BoxKind kind) =>
                Box(x - sx / 2, 0, z - sz / 2, x + sx / 2, height, z + sz / 2, kind);

            public void AddLoot(LootType type, float x, float y, float z, int qty) =>
                Loot.Add(new LootSpawn((ushort)Loot.Count, type, new Vector3(x, y, z), qty));

            /// <summary>xorshift32: identical on every runtime (System.Random is not guaranteed to be).</summary>
            public float Next()
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return (_state & 0xFFFFFF) / (float)0x1000000;
            }

            public float Range(float min, float max) => min + (max - min) * Next();

            public T Pick<T>(T[] items) => items[(int)(Next() * items.Length) % items.Length];
        }

        private enum Side
        {
            South,
            North,
            West,
            East,
        }

        private static MapLayout Generate(bool v2)
        {
            var b = new Builder();
            var districts = new List<District>
            {
                new District("Char Gate Plaza", -45, -45, 45, 45),
                new District("Old City Bazaar", -300, AvenueHalfWidth, -AvenueHalfWidth, 300),
                new District("Hi-Tech Towers", AvenueHalfWidth, AvenueHalfWidth, 300, 300),
                new District("Residential Colony", -300, -300, -AvenueHalfWidth, -AvenueHalfWidth),
                new District("Lakeside", AvenueHalfWidth, -300, 300, -AvenueHalfWidth),
            };

            // v1 districts first and in this order: they draw from the shared random sequence.
            Plaza(b);
            Bazaar(b);
            Towers(b);
            Residential(b);
            Lakeside(b);
            Viaduct(b);
            AvenueSpawns(b);
            if (!v2) return new MapLayout(Version, HalfSize, b.Boxes, b.Loot, b.Kinds, districts, b.Spawns);

            const float h = HalfSizeV2;
            districts.Add(new District("Industrial Estate", -h, HalfSize, h, h));
            districts.Add(new District("Outskirts", -h, -h, h, -HalfSize));
            districts.Add(new District("Musi Riverside", HalfSize, -HalfSize, h, HalfSize));
            districts.Add(new District("Logistics Park", -h, -HalfSize, -HalfSize, HalfSize));
            River(b);
            Industrial(b);
            Logistics(b);
            Outskirts(b);
            Farmland(b);
            ViaductExtension(b);
            RingSpawns(b);
            return new MapLayout(VersionV2, h, b.Boxes, b.Loot, b.Kinds, districts, b.Spawns);
        }

        /// <summary>
        /// One house from the building kit at the origin (13 x 11 m, door north, stairs inside along the east wall).
        /// Test fixture for the kit's walkability; not a published map.
        /// </summary>
        public static MapLayout KitSample()
        {
            var b = new Builder();
            Building(b, 0, 0, 13, 11, Side.North, Stairs.Inside, BoxKind.House, Loadout.Residential);
            return new MapLayout("kit-sample", 40, b.Boxes, b.Loot, b.Kinds);
        }

        // ---- Districts ----

        /// <summary>A four-towered gate with arches overhead (walk under, climb nothing), low walls and cover.</summary>
        private static void Plaza(Builder b)
        {
            const float half = 9f, tower = 4f, height = 26f;
            foreach (float sx in new[] { -1f, 1f })
                foreach (float sz in new[] { -1f, 1f })
                    b.Column(sx * half, sz * half, tower, tower, height, BoxKind.Landmark);
            // Arches between the towers (overhead: shots blocked, walking under allowed) and the upper gallery.
            b.Box(-half, 11, -half - 1.5f, half, 13, -half + 1.5f, BoxKind.Landmark);
            b.Box(-half, 11, half - 1.5f, half, 13, half + 1.5f, BoxKind.Landmark);
            b.Box(-half - 1.5f, 11, -half, -half + 1.5f, 13, half, BoxKind.Landmark);
            b.Box(half - 1.5f, 11, -half, half + 1.5f, 13, half, BoxKind.Landmark);
            b.Box(-half, 16, -half, half, 17, half, BoxKind.Landmark);

            // Ring of fountain walls and planters: cover around the landmark.
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6;
                float x = (float)Math.Sin(a) * 26, z = (float)Math.Cos(a) * 26;
                b.Column(x, z, 3.5f, 1f, 1.1f, BoxKind.Cover);
            }
            b.AddLoot(LootType.Weapon, 0, 0, 0, WeaponCatalog.Marksman);
            b.AddLoot(LootType.Armor, 20, 0, 0, 100);
            b.AddLoot(LootType.Medkit, -20, 0, 0, 2);
        }

        /// <summary>Dense one-storey shops in 2x2 blocks along narrow lanes, stalls, some rooftop access.</summary>
        private static void Bazaar(Builder b)
        {
            const float block = 30f, lane = 6f;
            for (float bz = AvenueHalfWidth + 8; bz + block <= HalfSize - 6; bz += block + lane)
            {
                for (float bx = -HalfSize + 6; bx + block <= -AvenueHalfWidth - 8; bx += block + lane)
                {
                    if (Math.Abs(bx + block / 2) < 50 && bz < 50) continue; // keep the plaza approach open
                    for (int i = 0; i < 4; i++)
                    {
                        float cx = bx + (i % 2 == 0 ? 7.5f : 22.5f), cz = bz + (i < 2 ? 7.5f : 22.5f);
                        Side door = i % 2 == 0 ? Side.West : Side.East;
                        bool stairs = b.Next() < 0.3f && door == Side.West; // east-side stairs would block an east door
                        Building(b, cx, cz, 12, 12, door, stairs ? Stairs.Outside : Stairs.None, BoxKind.Shop, district: Loadout.Bazaar);
                    }
                    // Stalls in the lane south of the block.
                    for (float sx = bx + 2; sx < bx + block - 2; sx += 6)
                        if (b.Next() < 0.6f) b.Box(sx, 0, bz - lane + 1, sx + 3, 2.4f, bz - 2.5f, BoxKind.Stall);
                }
            }
        }

        /// <summary>Tall offices (solid), parking with cars as cover, a few enterable gatehouses.</summary>
        private static void Towers(Builder b)
        {
            float[] xs = { 60, 130, 210, 270 };
            float[] zs = { 70, 150, 230 };
            foreach (float x in xs)
            {
                foreach (float z in zs)
                {
                    if (Math.Abs(x - 150) < 20) continue; // viaduct corridor
                    float w = b.Range(22, 34), d = b.Range(22, 34), h = b.Range(40, 80);
                    b.Column(x, z, w, d, h, BoxKind.Office);
                    // Parking in front: rows of cars.
                    for (int c = 0; c < 6; c++)
                        if (b.Next() < 0.7f) b.Column(x - w / 2 + 3 + c * 4, z - d / 2 - 7, 2.0f, 4.5f, 1.5f, BoxKind.Car);
                    float gx = x + w / 2 + 8;
                    if (Math.Abs(gx - 150) > 10) // keep gatehouses clear of the viaduct pillars
                        Building(b, gx, z - d / 2 - 4, 8, 6, Side.South, Stairs.None, BoxKind.Shop, district: Loadout.Towers);
                }
            }
        }

        /// <summary>Two-storey-tall houses with stairs inside to a walkable roof, behind compound walls.</summary>
        private static void Residential(Builder b)
        {
            const float plotW = 22f, plotD = 20f;
            for (float pz = -HalfSize + 8; pz + plotD <= -AvenueHalfWidth - 6; pz += plotD + 4)
            {
                for (float px = -HalfSize + 8; px + plotW <= -AvenueHalfWidth - 6; px += plotW + 4)
                {
                    if (px > -60 && pz > -60) continue; // plaza approach
                    if (b.Next() < 0.15f) continue;      // empty lot
                    // Compound wall with a gate gap on the north side (towards the street grid).
                    const float t = 0.25f, wh = 1.4f;
                    float x0 = px, x1 = px + plotW, z0 = pz, z1 = pz + plotD, mid = px + plotW / 2;
                    b.Box(x0, 0, z0, x1, wh, z0 + t, BoxKind.CompoundWall);
                    b.Box(x0, 0, z0, x0 + t, wh, z1, BoxKind.CompoundWall);
                    b.Box(x1 - t, 0, z0, x1, wh, z1, BoxKind.CompoundWall);
                    b.Box(x0, 0, z1 - t, mid - 1.8f, wh, z1, BoxKind.CompoundWall);
                    b.Box(mid + 1.8f, 0, z1 - t, x1, wh, z1, BoxKind.CompoundWall);
                    Building(b, mid, pz + plotD / 2 - 1, 13, 11, Side.North, Stairs.Inside, BoxKind.House, district: Loadout.Residential);
                }
            }
        }

        /// <summary>A lake (impassable water) with a tree-lined promenade, kiosks and boat sheds.</summary>
        private static void Lakeside(Builder b)
        {
            const float cx = 170, cz = -170, rx = 80, rz = 60;
            // Water as horizontal strips approximating an ellipse; 1.3 m high so it cannot be stepped or jumped onto.
            for (float z = cz - rz; z < cz + rz; z += 6)
            {
                float zz = (z + 3 - cz) / rz;
                float half = rx * (float)Math.Sqrt(Math.Max(0, 1 - zz * zz));
                if (half < 4) continue;
                b.Box(cx - half, 0, z, cx + half, 1.3f, z + 6, BoxKind.Water);
            }
            // Promenade trees: trunk (cover) and canopy overhead (blocks shots, walk under).
            for (int i = 0; i < 36; i++)
            {
                double a = i * Math.PI * 2 / 36;
                float x = cx + (float)Math.Sin(a) * (rx + 10), z = cz + (float)Math.Cos(a) * (rz + 10);
                if (Math.Abs(x - 150) < 8) continue; // viaduct pillars
                b.Column(x, z, 0.7f, 0.7f, 3f, BoxKind.Tree);
                b.Box(x - 2.2f, 3f, z - 2.2f, x + 2.2f, 5.5f, z + 2.2f, BoxKind.Tree);
            }
            // Kiosks and boat sheds around the shore.
            foreach ((float x, float z) in new[] { (70f, -80f), (270f, -90f), (75f, -255f), (265f, -260f), (170f, -250f) })
                Building(b, x, z, 9, 7, Side.North, Stairs.Outside, BoxKind.Shop, district: Loadout.Lakeside);
            b.AddLoot(LootType.Weapon, cx, 0, cz + rz + 18, WeaponCatalog.Sniper); // north promenade
        }

        /// <summary>Elevated metro line along x = 150: pillars at street level, a deck overhead (blocks shots).</summary>
        private static void Viaduct(Builder b)
        {
            const float x = 150, deckY = 9f;
            for (float z = -HalfSize + 10; z <= HalfSize - 10; z += 25) b.Column(x, z, 2.2f, 2.2f, deckY, BoxKind.Pillar);
            b.Box(x - 4, deckY, -HalfSize + 5, x + 4, deckY + 1.4f, HalfSize - 5, BoxKind.Viaduct);
        }

        /// <summary>Spawns spread along the two avenues and the edges, clear of buildings.</summary>
        private static void AvenueSpawns(Builder b)
        {
            for (float t = -270; t <= 270; t += 30)
            {
                if (Math.Abs(t) < 60) continue; // not inside the plaza
                b.Spawns.Add(new Vector3(0, 0, t));
                b.Spawns.Add(new Vector3(t, 0, 0));
            }
        }

        // ---- hyd-city-v2 ring ----

        private static bool OnRiver(float x) => x > RiverWest - 4 && x < RiverEast + 4;

        private static bool NearBridge(float z, float margin) => Array.Exists(BridgeZs, bz => Math.Abs(z - bz) < BridgeHalfWidth + margin);

        /// <summary>
        /// The river along x = 340..368, the whole length of the map, as impassable water strips with gaps where the
        /// bridges are. A bridge is a road deck with railings on both sides.
        /// </summary>
        private static void River(Builder b)
        {
            const float h = HalfSizeV2;
            var cuts = new List<float> { -h };
            foreach (float z in BridgeZs)
            {
                cuts.Add(z - BridgeHalfWidth);
                cuts.Add(z + BridgeHalfWidth);
            }
            cuts.Add(h);
            for (int i = 0; i + 1 < cuts.Count; i += 2)
                for (float z = cuts[i]; z < cuts[i + 1]; z += 30)
                    b.Box(RiverWest, 0, z, RiverEast, 1.3f, Math.Min(z + 30, cuts[i + 1]), BoxKind.Water);
            foreach (float z in BridgeZs)
            {
                b.Box(RiverWest - 4, 0, z - BridgeHalfWidth, RiverEast + 4, 0.05f, z + BridgeHalfWidth, BoxKind.Bridge);
                b.Box(RiverWest - 4, 0, z - BridgeHalfWidth, RiverEast + 4, 1.1f, z - BridgeHalfWidth + 0.4f, BoxKind.Railing);
                b.Box(RiverWest - 4, 0, z + BridgeHalfWidth - 0.4f, RiverEast + 4, 1.1f, z + BridgeHalfWidth, BoxKind.Railing);
            }
            // Ghats: steps and walls along the city bank (cover), clear of the bridges.
            for (float z = -h + 20; z < h - 20; z += 24)
                if (!NearBridge(z, 6)) b.Box(RiverWest - 6, 0, z, RiverWest - 5, 1.2f, z + 8, BoxKind.Cover);
        }

        /// <summary>
        /// A large one-storey shed (walls 7 m, roof not reachable) with a wide door on two sides, racks inside as cover
        /// and loot between the racks.
        /// </summary>
        private static void Warehouse(Builder b, float x, float z, float sx, float sz, Loadout district)
        {
            const float t = 0.4f, wall = 7f, door = 3f;
            float x0 = x - sx / 2, x1 = x + sx / 2, z0 = z - sz / 2, z1 = z + sz / 2;
            // North and south walls each have a wide doorway in the middle.
            b.Box(x0, 0, z0, x - door, wall, z0 + t, BoxKind.Warehouse);
            b.Box(x + door, 0, z0, x1, wall, z0 + t, BoxKind.Warehouse);
            b.Box(x0, 0, z1 - t, x - door, wall, z1, BoxKind.Warehouse);
            b.Box(x + door, 0, z1 - t, x1, wall, z1, BoxKind.Warehouse);
            b.Box(x0, 0, z0, x0 + t, wall, z1, BoxKind.Warehouse);
            b.Box(x1 - t, 0, z0, x1, wall, z1, BoxKind.Warehouse);
            b.Box(x0, wall, z0, x1, wall + t, z1, BoxKind.Roof);
            // Two rows of racks, leaving the centre aisle and the doorways clear.
            for (float rx = x0 + 3; rx + 2 < x1 - 2; rx += 6)
            {
                if (Math.Abs(rx + 1 - x) < door + 1.5f) continue;
                b.Box(rx, 0, z0 + 3, rx + 2, 2.6f, z - 3, BoxKind.Cover);
                b.Box(rx, 0, z + 3, rx + 2, 2.6f, z1 - 3, BoxKind.Cover);
            }
            b.AddLoot(Pick(b, district, out int qty), x, 0, z, qty);
            b.AddLoot(Pick(b, district, out int qty2), x0 + 2, 0, z, qty2);
            if (b.Next() < 0.5f) b.AddLoot(Pick(b, district, out int qty3), x1 - 2, 0, z, qty3);
        }

        /// <summary>A stack of shipping containers (2.6 m each), one to three high: cover, and something to climb on.</summary>
        private static void Containers(Builder b, float x, float z, bool alongX)
        {
            int high = 1 + (int)(b.Next() * 3);
            float sx = alongX ? 12f : 2.5f, sz = alongX ? 2.5f : 12f;
            for (int i = 0; i < high; i++) b.Box(x - sx / 2, i * 2.6f, z - sz / 2, x + sx / 2, (i + 1) * 2.6f, z + sz / 2, BoxKind.Container);
        }

        /// <summary>North band: warehouses in rows, container yards between them, and a factory with a tall chimney.</summary>
        private static void Industrial(Builder b)
        {
            const float h = HalfSizeV2;
            for (float z = HalfSize + 40; z < h - 30; z += 60)
            {
                for (float x = -h + 50; x < h - 50; x += 70)
                {
                    if (Math.Abs(x) < 25 || Math.Abs(x - 150) < 25 || OnRiver(x) || OnRiver(x - 25) || OnRiver(x + 25)) continue; // avenue, viaduct, river
                    if (Math.Abs(x + 200) < 60 && z > h - 75) continue; // the factory's plot
                    if (b.Next() < 0.65f) Warehouse(b, x, z, b.Range(34, 44), b.Range(20, 26), Loadout.Industrial);
                    else
                    {
                        Containers(b, x - 10, z - 6, alongX: true);
                        Containers(b, x + 8, z + 6, alongX: true);
                        b.AddLoot(Pick(b, Loadout.Industrial, out int qty), x, 0, z, qty);
                    }
                }
            }
            // The factory: a big shed and its chimney, a landmark visible from across the map.
            Warehouse(b, -210, h - 45, 50, 30, Loadout.Industrial);
            b.Column(-178, h - 45, 5, 5, 45, BoxKind.Chimney);
        }

        /// <summary>West band: container stacks in yard rows, two warehouses and a depot of parked trucks.</summary>
        private static void Logistics(Builder b)
        {
            const float h = HalfSizeV2;
            for (float z = -HalfSize + 30; z < HalfSize - 20; z += 34)
            {
                if (Math.Abs(z) < 25) continue; // avenue
                for (float x = -h + 25; x < -HalfSize - 15; x += 30)
                {
                    float r = b.Next();
                    if (r < 0.55f) Containers(b, x, z, alongX: b.Next() < 0.5f);
                    else if (r < 0.75f) b.AddLoot(Pick(b, Loadout.Industrial, out int qty), x, 0, z, qty);
                }
            }
            Warehouse(b, -375, 150, 40, 24, Loadout.Industrial);
            Warehouse(b, -375, -150, 40, 24, Loadout.Industrial);
            for (int i = 0; i < 6; i++) b.Column(-330, -60 + i * 8, 3f, 8f, 3.2f, BoxKind.Car); // trucks
        }

        /// <summary>South band: open fields with low rocks, lone trees, haystacks, and a few farmhouses with roof stairs.</summary>
        private static void Outskirts(Builder b)
        {
            const float h = HalfSizeV2;
            for (float z = -h + 15; z < -HalfSize - 10; z += 22)
            {
                for (float x = -h + 15; x < h - 15; x += 22)
                {
                    if (Math.Abs(x) < 14 || OnRiver(x)) continue; // the road south, and the river
                    float r = b.Next();
                    float jx = x + b.Range(-6, 6), jz = z + b.Range(-6, 6);
                    if (r < 0.18f) b.Column(jx, jz, b.Range(1.5f, 3.5f), b.Range(1.5f, 3f), b.Range(0.9f, 1.6f), BoxKind.Rock);
                    else if (r < 0.3f)
                    {
                        b.Column(jx, jz, 0.7f, 0.7f, 3f, BoxKind.Tree);
                        b.Box(jx - 2.4f, 3f, jz - 2.4f, jx + 2.4f, 5.8f, jz + 2.4f, BoxKind.Tree);
                    }
                    else if (r < 0.36f) b.Column(jx, jz, 3f, 2f, 1.8f, BoxKind.Field); // haystack
                    else if (r < 0.42f) b.AddLoot(Pick(b, Loadout.Outskirts, out int qty), x, 0, z, qty); // in the open
                }
            }
            foreach ((float x, float z) in new[] { (-300f, -390f), (-120f, -410f), (90f, -380f), (250f, -405f) })
                Building(b, x, z, 13, 11, Side.North, Stairs.Outside, BoxKind.House, Loadout.Outskirts);
        }

        /// <summary>East bank beyond the river: fields and farmhouses, reached over the bridges.</summary>
        private static void Farmland(Builder b)
        {
            const float h = HalfSizeV2;
            for (float z = -h + 20; z < h - 20; z += 26)
            {
                for (float x = RiverEast + 12; x < h - 10; x += 26)
                {
                    if (NearBridge(z, 4)) continue; // keep the roads off the bridges clear
                    float r = b.Next();
                    if (r < 0.15f) b.Column(x + b.Range(-5, 5), z + b.Range(-5, 5), b.Range(1.5f, 3f), b.Range(1.5f, 3f), b.Range(0.9f, 1.5f), BoxKind.Rock);
                    else if (r < 0.25f) b.Column(x, z, 3f, 2f, 1.8f, BoxKind.Field);
                    else if (r < 0.32f) b.AddLoot(Pick(b, Loadout.Outskirts, out int qty), x, 0, z, qty);
                }
            }
            foreach (float z in new[] { -330f, -110f, 110f, 330f })
                Building(b, 410, z, 13, 11, Side.West, Stairs.Outside, BoxKind.House, Loadout.Outskirts);
        }

        /// <summary>The metro line continues north and south to the map edge.</summary>
        private static void ViaductExtension(Builder b)
        {
            const float x = 150, deckY = 9f, h = HalfSizeV2;
            for (float z = HalfSize + 10; z <= h - 10; z += 25) b.Column(x, z, 2.2f, 2.2f, deckY, BoxKind.Pillar);
            for (float z = -HalfSize - 10; z >= -h + 10; z -= 25) b.Column(x, z, 2.2f, 2.2f, deckY, BoxKind.Pillar);
            b.Box(x - 4, deckY, HalfSize - 5, x + 4, deckY + 1.4f, h - 5, BoxKind.Viaduct);
            b.Box(x - 4, deckY, -h + 5, x + 4, deckY + 1.4f, -HalfSize + 5, BoxKind.Viaduct);
        }

        /// <summary>Spawns along the avenues continue into the ring (until the plane drop arrives in part C6).</summary>
        private static void RingSpawns(Builder b)
        {
            foreach (float t in new[] { -420f, -360f, 360f, 420f })
            {
                b.Spawns.Add(new Vector3(0, 0, t));
                if (!OnRiver(t)) b.Spawns.Add(new Vector3(t, 0, 0));
            }
            b.Spawns.Add(new Vector3(RiverEast + 30, 0, 0)); // across the middle bridge
        }

        // ---- Building kit ----

        private enum Stairs
        {
            None,
            Outside,
            Inside,
        }

        private enum Loadout
        {
            Bazaar,
            Towers,
            Residential,
            Lakeside,
            Industrial,
            Outskirts,
        }

        /// <summary>
        /// One storey: four walls with a 2 m doorway on <paramref name="door"/>, a roof slab, optional stairs
        /// (outside along the east wall, or inside along the east wall with a roof opening above), and loot.
        /// </summary>
        private static void Building(Builder b, float x, float z, float sx, float sz, Side door, Stairs stairs, BoxKind kind, Loadout district)
        {
            const float t = MapLayout.WallThickness, h = MapLayout.StoreyHeight, doorHalf = 1f;
            float minX = x - sx / 2, maxX = x + sx / 2, minZ = z - sz / 2, maxZ = z + sz / 2;

            void WallX(float zMin, float zMax, bool hasDoor) // wall running along X at z
            {
                if (!hasDoor) b.Box(minX, 0, zMin, maxX, h, zMax, kind);
                else
                {
                    b.Box(minX, 0, zMin, x - doorHalf, h, zMax, kind);
                    b.Box(x + doorHalf, 0, zMin, maxX, h, zMax, kind);
                }
            }
            void WallZ(float xMin, float xMax, bool hasDoor) // wall running along Z at x
            {
                if (!hasDoor) b.Box(xMin, 0, minZ, xMax, h, maxZ, kind);
                else
                {
                    b.Box(xMin, 0, minZ, xMax, h, z - doorHalf, kind);
                    b.Box(xMin, 0, z + doorHalf, xMax, h, maxZ, kind);
                }
            }

            WallX(minZ, minZ + t, door == Side.South);
            WallX(maxZ - t, maxZ, door == Side.North);
            WallZ(minX, minX + t, door == Side.West);
            WallZ(maxX - t, maxX, door == Side.East);

            float rise = MapLayout.RoofTop / MapLayout.StairSteps;
            float run = (sz - 1f) / MapLayout.StairSteps;
            if (stairs == Stairs.Inside)
            {
                // Steps inside along the east wall, climbing north; the roof has an opening over the stair strip.
                float sx0 = maxX - t - 1.5f, sx1 = maxX - t;
                for (int i = 0; i < MapLayout.StairSteps; i++)
                {
                    float z0 = minZ + t + 0.2f + i * (sz - 2 * t - 0.4f) / MapLayout.StairSteps;
                    float z1 = z0 + (sz - 2 * t - 0.4f) / MapLayout.StairSteps;
                    b.Box(sx0, 0, z0, sx1, rise * (i + 1), z1, BoxKind.Stair);
                }
                b.Box(minX, h, minZ, sx0, h + t, maxZ, BoxKind.Roof);            // roof west of the opening
                b.Box(sx0, h, maxZ - t - 0.2f, maxX, h + t, maxZ, BoxKind.Roof); // strip north of the opening (top landing)
            }
            else
            {
                b.Box(minX, h, minZ, maxX, h + t, maxZ, BoxKind.Roof);
                if (stairs == Stairs.Outside)
                {
                    for (int i = 0; i < MapLayout.StairSteps; i++)
                    {
                        float z0 = minZ + 0.5f + i * run;
                        b.Box(maxX, 0, z0, maxX + 1.5f, rise * (i + 1), z0 + run, BoxKind.Stair);
                    }
                }
            }

            // Loot: one item inside, sometimes one on the roof when it is reachable.
            b.AddLoot(Pick(b, district, out int qty), x - sx / 4, 0, z, qty);
            if (stairs != Stairs.None && b.Next() < 0.5f) b.AddLoot(LootType.Armor, x - sx / 4, MapLayout.RoofTop, z, b.Next() < 0.3f ? 100 : 50);
        }

        private static LootType Pick(Builder b, Loadout district, out int qty)
        {
            float r = b.Next();
            // Weapons by district: close-quarters in the bazaar, long range near towers and the lake.
            byte[] weapons = district switch
            {
                Loadout.Bazaar => new[] { WeaponCatalog.Smg, WeaponCatalog.Shotgun, WeaponCatalog.Pistol, WeaponCatalog.Rifle },
                Loadout.Towers => new[] { WeaponCatalog.Rifle, WeaponCatalog.Marksman, WeaponCatalog.Smg },
                Loadout.Residential => new[] { WeaponCatalog.Pistol, WeaponCatalog.Shotgun, WeaponCatalog.Rifle, WeaponCatalog.Smg },
                Loadout.Industrial => new[] { WeaponCatalog.Rifle, WeaponCatalog.Shotgun, WeaponCatalog.Smg, WeaponCatalog.Marksman },
                Loadout.Outskirts => new[] { WeaponCatalog.Sniper, WeaponCatalog.Marksman, WeaponCatalog.Rifle, WeaponCatalog.Pistol },
                _ => new[] { WeaponCatalog.Rifle, WeaponCatalog.Marksman, WeaponCatalog.Pistol },
            };
            if (r < 0.45f)
            {
                qty = b.Pick(weapons);
                return LootType.Weapon;
            }
            if (r < 0.75f)
            {
                qty = 60;
                return LootType.Ammo;
            }
            if (r < 0.9f)
            {
                qty = 1;
                return LootType.Medkit;
            }
            qty = r < 0.97f ? 50 : 100;
            return LootType.Armor;
        }
    }
}
