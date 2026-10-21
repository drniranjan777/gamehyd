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
    /// Server-side collision view of a map version. The Unity scene for the same map version must
    /// match these boxes; a mismatch is a release-blocking defect.
    /// </summary>
    public sealed class MapLayout
    {
        public MapLayout(string version, float halfSize, IReadOnlyList<Aabb> obstacles)
        {
            Version = version;
            HalfSize = halfSize;
            Obstacles = obstacles;
        }

        public string Version { get; }
        public float HalfSize { get; }
        public IReadOnlyList<Aabb> Obstacles { get; }

        /// <summary>Phase 0 greybox: one city block (buildings, a market row, cover) scaled to the map size.</summary>
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
            return new MapLayout($"greybox-block-{halfSize:0}", halfSize, boxes);
        }

        public const float WallThickness = 0.3f;
        public const float StoreyHeight = 3.2f;

        /// <summary>Four walls with a doorway centred in the south (-Z) wall, plus a roof slab overhead.</summary>
        private static void AddEnterableBuilding(List<Aabb> boxes, float x, float z, float sizeX, float sizeZ, float doorWidth)
        {
            float t = WallThickness, h = StoreyHeight;
            float minX = x - sizeX / 2, maxX = x + sizeX / 2, minZ = z - sizeZ / 2, maxZ = z + sizeZ / 2;
            boxes.Add(new Aabb(new Vector3(minX, 0, maxZ - t), new Vector3(maxX, h, maxZ)));     // north
            boxes.Add(new Aabb(new Vector3(minX, 0, minZ), new Vector3(minX + t, h, maxZ)));     // west
            boxes.Add(new Aabb(new Vector3(maxX - t, 0, minZ), new Vector3(maxX, h, maxZ)));     // east
            boxes.Add(new Aabb(new Vector3(minX, 0, minZ), new Vector3(x - doorWidth / 2, h, minZ + t))); // south, left of door
            boxes.Add(new Aabb(new Vector3(x + doorWidth / 2, 0, minZ), new Vector3(maxX, h, minZ + t))); // south, right of door
            boxes.Add(new Aabb(new Vector3(minX, h, minZ), new Vector3(maxX, h + t, maxZ)));     // roof slab (blocks shots, not walking)
        }

        public Vector3 SpawnPoint(int index, int count)
        {
            double angle = 2 * Math.PI * index / Math.Max(1, count);
            float r = HalfSize * 0.75f;
            return new Vector3((float)(Math.Sin(angle) * r), 0, (float)(Math.Cos(angle) * r));
        }
    }
}
