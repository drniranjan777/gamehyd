#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace RWBR.Shared
{
    /// <summary>
    /// Uniform XZ grid over a map's obstacles so movement and line-of-sight queries touch only nearby boxes
    /// (thousands of boxes on the city map). Queries reuse per-thread scratch buffers; results are the same as a
    /// brute-force scan (tested).
    /// </summary>
    public sealed class SpatialGrid
    {
        public const float CellSize = 16f;

        [ThreadStatic] private static int[]? _marks;
        [ThreadStatic] private static int _stamp;

        private readonly IReadOnlyList<Aabb> _boxes;
        private readonly List<int>[] _cells;
        private readonly float _origin;
        private readonly int _n;

        public SpatialGrid(IReadOnlyList<Aabb> boxes, float halfSize)
        {
            _boxes = boxes;
            _origin = -halfSize;
            _n = Math.Max(1, (int)Math.Ceiling(halfSize * 2 / CellSize));
            _cells = new List<int>[_n * _n];
            for (int i = 0; i < boxes.Count; i++)
            {
                Aabb b = boxes[i];
                int x0 = Cell(b.Min.X), x1 = Cell(b.Max.X), z0 = Cell(b.Min.Z), z1 = Cell(b.Max.Z);
                for (int cz = z0; cz <= z1; cz++)
                    for (int cx = x0; cx <= x1; cx++)
                        (_cells[cz * _n + cx] ??= new List<int>()).Add(i);
            }
        }

        private int Cell(float v) => Math.Max(0, Math.Min(_n - 1, (int)Math.Floor((v - _origin) / CellSize)));

        private int[] Marks()
        {
            if (_marks == null || _marks.Length < _boxes.Count) _marks = new int[Math.Max(_boxes.Count, 64)];
            if (++_stamp == int.MaxValue)
            {
                Array.Clear(_marks, 0, _marks.Length);
                _stamp = 1;
            }
            return _marks;
        }

        /// <summary>Indices of boxes whose XZ footprint may overlap the rectangle (each once). Clears <paramref name="results"/>.</summary>
        public void Query(float minX, float minZ, float maxX, float maxZ, List<int> results)
        {
            results.Clear();
            int[] marks = Marks();
            int stamp = _stamp;
            int x0 = Cell(minX), x1 = Cell(maxX), z0 = Cell(minZ), z1 = Cell(maxZ);
            for (int cz = z0; cz <= z1; cz++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    List<int>? cell = _cells[cz * _n + cx];
                    if (cell == null) continue;
                    foreach (int i in cell)
                    {
                        if (marks[i] == stamp) continue;
                        marks[i] = stamp;
                        results.Add(i);
                    }
                }
            }
        }

        /// <summary>Distance along the ray to the nearest box, or maxDistance. Walks grid cells front to back.</summary>
        public float RayDistance(Vector3 origin, Vector3 dir, float maxDistance)
        {
            float best = maxDistance;
            int[] marks = Marks();
            int stamp = _stamp;
            float extent = _n * CellSize;
            float lx = origin.X - _origin, lz = origin.Z - _origin;

            // Outside the grid (e.g. a camera beyond the map edge): fall back to testing everything.
            if (lx < 0 || lz < 0 || lx >= extent || lz >= extent)
            {
                for (int i = 0; i < _boxes.Count; i++) Test(i);
                return best;
            }

            int cx = (int)(lx / CellSize), cz = (int)(lz / CellSize);
            int stepX = dir.X > 0 ? 1 : -1, stepZ = dir.Z > 0 ? 1 : -1;
            float tDeltaX = Math.Abs(dir.X) < 1e-9f ? float.PositiveInfinity : CellSize / Math.Abs(dir.X);
            float tDeltaZ = Math.Abs(dir.Z) < 1e-9f ? float.PositiveInfinity : CellSize / Math.Abs(dir.Z);
            float tMaxX = Math.Abs(dir.X) < 1e-9f ? float.PositiveInfinity
                : ((stepX > 0 ? (cx + 1) * CellSize : cx * CellSize) - lx) / dir.X;
            float tMaxZ = Math.Abs(dir.Z) < 1e-9f ? float.PositiveInfinity
                : ((stepZ > 0 ? (cz + 1) * CellSize : cz * CellSize) - lz) / dir.Z;

            while (true)
            {
                List<int>? cell = _cells[cz * _n + cx];
                if (cell != null) foreach (int i in cell) Test(i);

                float next = Math.Min(tMaxX, tMaxZ);
                if (next > best) break; // anything in later cells is further than the hit we have
                if (tMaxX < tMaxZ)
                {
                    cx += stepX;
                    tMaxX += tDeltaX;
                }
                else
                {
                    cz += stepZ;
                    tMaxZ += tDeltaZ;
                }
                if (cx < 0 || cz < 0 || cx >= _n || cz >= _n) break;
            }
            return best;

            void Test(int i)
            {
                if (marks[i] == stamp) return;
                marks[i] = stamp;
                float? t = Simulation.RayAabb(origin, dir, _boxes[i], best);
                if (t.HasValue && t.Value < best) best = t.Value;
            }
        }
    }
}
