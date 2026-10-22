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

    /// <summary>
    /// Server-side collision and loot view of a map version. The Unity scene for the same map version must
    /// match these boxes; a mismatch is a release-blocking defect.
    /// </summary>
    public sealed class MapLayout
    {
        public MapLayout(string version, float halfSize, IReadOnlyList<Aabb> obstacles, IReadOnlyList<LootSpawn>? loot = null)
        {
            Version = version;
            HalfSize = halfSize;
            Obstacles = obstacles;
            Loot = loot ?? Array.Empty<LootSpawn>();
        }

        public string Version { get; }
        public float HalfSize { get; }
        public IReadOnlyList<Aabb> Obstacles { get; }
        public IReadOnlyList<LootSpawn> Loot { get; }

        public const float WallThickness = 0.3f;
        public const float StoreyHeight = 3.2f;
        public const float RoofTop = StoreyHeight + WallThickness;
        public const int StairSteps = 9;

        /// <summary>
        /// Greybox city block scaled to the map size: an enterable building with outside stairs to its roof,
        /// two apartment blocks, market stall rows, low cover and loot spawns.
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

            return new MapLayout($"greybox-block-{halfSize:0}-v2", halfSize, boxes, loot);
        }

        /// <summary>
        /// Four walls with a doorway centred in the south (-Z) wall, a roof slab, and a stair of solid steps
        /// along the outside of the east wall leading up to roof level.
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

        /// <summary>Resolves a published map version such as "greybox-block-40-v2"; false when unknown.</summary>
        public static bool TryFromVersion(string version, out MapLayout layout)
        {
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
            double angle = 2 * Math.PI * index / Math.Max(1, count);
            float r = HalfSize * 0.75f;
            return new Vector3((float)(Math.Sin(angle) * r), 0, (float)(Math.Cos(angle) * r));
        }
    }
}
