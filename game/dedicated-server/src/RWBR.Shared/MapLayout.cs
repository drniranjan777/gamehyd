#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace RWBR.Shared
{
    /// <summary>Axis-aligned box used for collision and line of sight.</summary>
    public readonly struct Aabb
    {
        public Aabb(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        public Vector3 Min { get; }
        public Vector3 Max { get; }

        public static Aabb FromCenter(float x, float z, float sizeX, float sizeZ, float height) =>
            new Aabb(new Vector3(x - sizeX / 2, 0, z - sizeZ / 2), new Vector3(x + sizeX / 2, height, z + sizeZ / 2));
    }

    /// <summary>What a box represents. Gameplay ignores it; the client uses it for colour, LOD and sound.</summary>
    public enum BoxKind : byte
    {
        Generic = 0,
        Wall,
        Roof,
        Stair,
        Cover,
        Stall,
        Shop,
        House,
        Office,
        Landmark,
        Water,
        Viaduct,
        Pillar,
        Tree,
        Car,
        CompoundWall,
        // hyd-city-v2
        Warehouse,
        Container,
        Rock,
        Chimney,
        Railing,
        Bridge,
        Field,
    }

    /// <summary>A named area of a map (HUD location name, loot weighting).</summary>
    public readonly struct District
    {
        public District(string name, float minX, float minZ, float maxX, float maxZ)
        {
            Name = name;
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
        }

        public string Name { get; }
        public float MinX { get; }
        public float MinZ { get; }
        public float MaxX { get; }
        public float MaxZ { get; }

        public bool Contains(float x, float z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
    }

    /// <summary>
    /// Server-side collision and loot view of a map version. The Unity scene for the same map version is built from
    /// this same data, so client and server geometry cannot drift. Published versions are immutable.
    /// </summary>
    public sealed class MapLayout
    {
        public MapLayout(string version, float halfSize, IReadOnlyList<Aabb> obstacles, IReadOnlyList<LootSpawn>? loot = null,
            IReadOnlyList<BoxKind>? kinds = null, IReadOnlyList<District>? districts = null, IReadOnlyList<Vector3>? spawns = null)
        {
            if (kinds != null && kinds.Count != obstacles.Count) throw new ArgumentException("one kind per obstacle");
            Version = version;
            HalfSize = halfSize;
            Obstacles = obstacles;
            var resolved = new LootSpawn[loot?.Count ?? 0];
            for (int i = 0; i < resolved.Length; i++) resolved[i] = LootTable.Resolve(version, loot![i]);
            Loot = resolved;
            Kinds = kinds;
            Districts = districts ?? Array.Empty<District>();
            Spawns = spawns ?? Array.Empty<Vector3>();
            Grid = new SpatialGrid(obstacles, halfSize);
        }

        public string Version { get; }
        public float HalfSize { get; }
        public IReadOnlyList<Aabb> Obstacles { get; }
        public IReadOnlyList<LootSpawn> Loot { get; }
        public IReadOnlyList<District> Districts { get; }
        /// <summary>Hand-placed spawn points (empty: spawn on a ring).</summary>
        public IReadOnlyList<Vector3> Spawns { get; }
        public SpatialGrid Grid { get; }
        private IReadOnlyList<BoxKind>? Kinds { get; }

        public BoxKind KindOf(int obstacle) => Kinds != null ? Kinds[obstacle] : BoxKind.Generic;

        public string DistrictAt(float x, float z)
        {
            foreach (District d in Districts)
                if (d.Contains(x, z)) return d.Name;
            return "";
        }

        public const float WallThickness = 0.3f;
        public const float StoreyHeight = 3.2f;
        public const float RoofTop = StoreyHeight + WallThickness;
        public const int StairSteps = 9;

        /// <summary>
        /// Greybox city block scaled to the map size: an enterable building with outside stairs to its roof,
        /// two apartment blocks, market stall rows, low cover and loot spawns.
        /// Published as greybox-block-{size}-v2: the geometry below must never change.
        /// </summary>
        public static MapLayout GreyboxBlock(float halfSize)
        {
            float s = halfSize / 100f;
            var boxes = new List<Aabb>();
            AddEnterableBuilding(boxes, 0, 0, 16 * s, 12 * s, doorWidth: 2f);
            boxes.AddRange(new[]
            {
                Aabb.FromCenter(-40 * s, 30 * s, 20 * s, 10 * s, 18), // apartment block
                Aabb.FromCenter(40 * s, -30 * s, 20 * s, 10 * s, 18), // apartment block
                Aabb.FromCenter(35 * s, 40 * s, 30 * s, 4 * s, 3),    // market stalls row
                Aabb.FromCenter(-35 * s, -40 * s, 4 * s, 30 * s, 3),  // market stalls row
                Aabb.FromCenter(15 * s, 15 * s, 3, 1, 1.2f),          // low cover
                Aabb.FromCenter(-15 * s, -15 * s, 3, 1, 1.2f),        // low cover
                Aabb.FromCenter(-20 * s, 10 * s, 1, 3, 1.2f),         // low cover
                Aabb.FromCenter(20 * s, -10 * s, 1, 3, 1.2f),         // low cover
            });

            var loot = new List<LootSpawn>();
            void Add(LootType type, float x, float y, float z, int qty) =>
                loot.Add(new LootSpawn((ushort)loot.Count, type, new Vector3(x, y, z), qty));

            Add(LootType.Rifle, -4 * s, 0, 2 * s, 1);          // inside the building
            Add(LootType.Ammo, 4 * s, 0, 2 * s, 60);
            Add(LootType.Armor, 0, RoofTop, 0, 100);            // roof
            Add(LootType.Medkit, -4 * s, RoofTop, -3 * s, 1);
            Add(LootType.Rifle, 15 * s, 0, 13 * s, 1);          // behind cover
            Add(LootType.Medkit, -15 * s, 0, -13 * s, 1);
            Add(LootType.Medkit, -20 * s, 0, 6 * s, 1);
            Add(LootType.Ammo, 20 * s, 0, -6 * s, 60);
            Add(LootType.Ammo, 35 * s, 0, 36 * s, 60);          // markets
            Add(LootType.Armor, -31 * s, 0, -40 * s, 50);
            Add(LootType.Rifle, -40 * s, 0, 22 * s, 1);         // apartments
            Add(LootType.Ammo, 40 * s, 0, -22 * s, 60);

            var kinds = new List<BoxKind>();
            for (int i = 0; i < 6; i++) kinds.Add(i == 5 ? BoxKind.Roof : BoxKind.Wall);
            for (int i = 0; i < StairSteps; i++) kinds.Add(BoxKind.Stair);
            kinds.AddRange(new[] { BoxKind.House, BoxKind.House, BoxKind.Stall, BoxKind.Stall, BoxKind.Cover, BoxKind.Cover, BoxKind.Cover, BoxKind.Cover });

            return new MapLayout($"greybox-block-{halfSize:0}-v2", halfSize, boxes, loot, kinds);
        }

        /// <summary>
        /// Four walls with a doorway centred in the south (-Z) wall, a roof slab, and a stair of solid steps
        /// along the outside of the east wall leading up to roof level. (Greybox only; the city uses CityMap.)
        /// </summary>
        private static void AddEnterableBuilding(List<Aabb> boxes, float x, float z, float sizeX, float sizeZ, float doorWidth)
        {
            float t = WallThickness, h = StoreyHeight;
            float minX = x - sizeX / 2, maxX = x + sizeX / 2, minZ = z - sizeZ / 2, maxZ = z + sizeZ / 2;
            boxes.Add(new Aabb(new Vector3(minX, 0, maxZ - t), new Vector3(maxX, h, maxZ)));     // north
            boxes.Add(new Aabb(new Vector3(minX, 0, minZ), new Vector3(minX + t, h, maxZ)));     // west
            boxes.Add(new Aabb(new Vector3(maxX - t, 0, minZ), new Vector3(maxX, h, maxZ)));     // east
            boxes.Add(new Aabb(new Vector3(minX, 0, minZ), new Vector3(x - doorWidth / 2, h, minZ + t))); // south, left of door
            boxes.Add(new Aabb(new Vector3(x + doorWidth / 2, 0, minZ), new Vector3(maxX, h, minZ + t))); // south, right of door
            boxes.Add(new Aabb(new Vector3(minX, h, minZ), new Vector3(maxX, h + t, maxZ)));     // roof slab

            // Stairs: climb northwards along the east wall; the top step is level with the roof.
            float rise = RoofTop / StairSteps;
            float run = (sizeZ - 1f) / StairSteps;
            for (int i = 0; i < StairSteps; i++)
            {
                float z0 = minZ + 0.5f + i * run;
                boxes.Add(new Aabb(new Vector3(maxX, 0, z0), new Vector3(maxX + 1.5f, rise * (i + 1), z0 + run)));
            }
        }

        public const string TrainingRangeVersion = "training-range-v1";
        /// <summary>Training range firing line (z); target distances are measured from here.</summary>
        public const float RangeFiringLine = -110f;
        /// <summary>Target distances in metres from the firing line, one lane each (west to east).</summary>
        public static readonly float[] RangeDistances = { 10, 25, 50, 100, 200 };
        public static float RangeLaneX(int lane) => -16f + lane * 8f;

        /// <summary>
        /// Offline training range: a bench at the firing line with every catalog weapon and armour behind it (ammo is endless), five lanes
        /// with a target and a distance post at 10, 25, 50, 100 and 200 m, side walls and a high backstop.
        /// Client-only (no match server hosts it); the geometry is still fixed per version like every map.
        /// </summary>
        public static MapLayout TrainingRange()
        {
            const float line = RangeFiringLine;
            var boxes = new List<Aabb>();
            var kinds = new List<BoxKind>();
            void Add(Aabb box, BoxKind kind)
            {
                boxes.Add(box);
                kinds.Add(kind);
            }

            Add(Aabb.FromCenter(0, line + 1.5f, 16, 0.8f, 0.9f), BoxKind.Cover);      // bench
            Add(Aabb.FromCenter(0, 112, 90, 4, 12), BoxKind.Wall);                     // backstop
            Add(Aabb.FromCenter(-42, 0, 1, 230, 3), BoxKind.CompoundWall);            // side walls
            Add(Aabb.FromCenter(42, 0, 1, 230, 3), BoxKind.CompoundWall);
            Add(Aabb.FromCenter(0, -116, 85, 1, 3), BoxKind.CompoundWall);           // behind the firing line
            for (int lane = 0; lane < RangeDistances.Length; lane++)
            {
                float z = line + RangeDistances[lane];
                Add(Aabb.FromCenter(RangeLaneX(lane) - 2.5f, z, 0.25f, 0.25f, 2.6f), BoxKind.Pillar); // distance post
            }
            Add(Aabb.FromCenter(-26, line + 25, 4, 1, 1.2f), BoxKind.Cover);          // low cover for the moving targets
            Add(Aabb.FromCenter(26, line + 50, 4, 1, 1.2f), BoxKind.Cover);

            var loot = new List<LootSpawn>();
            for (int w = 0; w < WeaponCatalog.Count; w++)
                loot.Add(new LootSpawn((ushort)loot.Count, LootType.Weapon, new Vector3(-6.25f + w * 2.5f, 0, line - 1f), w));
            loot.Add(new LootSpawn((ushort)loot.Count, LootType.Armor, new Vector3(-9, 0, line - 1f), (int)GameRules.MaxArmor));

            return new MapLayout(TrainingRangeVersion, 140, boxes, loot, kinds, spawns: new[] { new Vector3(0, 0, line - 3.5f) });
        }

        /// <summary>Resolves a published map version ("greybox-block-40-v2", "hyd-city-v1"); false when unknown.</summary>
        public static bool TryFromVersion(string version, out MapLayout layout)
        {
            if (version == CityMap.Version)
            {
                layout = CityMap.Build();
                return true;
            }
            if (version == CityMap.VersionV2)
            {
                layout = CityMap.BuildV2();
                return true;
            }
            if (version == TrainingRangeVersion)
            {
                layout = TrainingRange();
                return true;
            }
            layout = GreyboxBlock(100f);
            string[] parts = (version ?? "").Split('-');
            if (parts.Length < 3 || parts[0] != "greybox" || parts[1] != "block" ||
                !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float size) ||
                size < 10 || size > 1000)
                return false;
            layout = GreyboxBlock(size);
            return layout.Version == version;
        }

        public Vector3 SpawnPoint(int index, int count)
        {
            if (Spawns.Count > 0)
            {
                // Spread players across the hand-placed points.
                int i = (int)((long)index * Spawns.Count / Math.Max(1, count)) % Spawns.Count;
                return Spawns[i];
            }
            double angle = 2 * Math.PI * index / Math.Max(1, count);
            float r = HalfSize * 0.75f;
            return new Vector3((float)(Math.Sin(angle) * r), 0, (float)(Math.Cos(angle) * r));
        }
    }
}
