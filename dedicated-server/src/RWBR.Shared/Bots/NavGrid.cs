#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RWBR.Shared.Bots
{
    /// <summary>
    /// Ground-level walkability for bots: a 2 m grid over the map where a cell is blocked if a box that a standing
    /// player can't step over or walk under overlaps it. Paths come from A* (8-way, no corner cutting) and are
    /// shortened by skipping waypoints with a clear straight line. Built once per map version and shared.
    /// Rooftops and upper floors are not navigated (bots stay on the ground).
    /// </summary>
    public sealed class NavGrid
    {
        public const float CellSize = 2f;
        private const float BodyRadius = 0.45f;
        private const int MaxExpansions = 60_000;
        private static readonly ConditionalWeakTable<MapLayout, NavGrid> Cache = new ConditionalWeakTable<MapLayout, NavGrid>();

        private readonly bool[] _blocked;
        private readonly int _size;
        private readonly float _origin;

        public static NavGrid For(MapLayout map) => Cache.GetValue(map, m => new NavGrid(m));

        private NavGrid(MapLayout map)
        {
            _origin = -map.HalfSize;
            _size = (int)Math.Ceiling(map.HalfSize * 2 / CellSize);
            _blocked = new bool[_size * _size];
            var hits = new List<int>();
            for (int z = 0; z < _size; z++)
            for (int x = 0; x < _size; x++)
            {
                Vector2 c = CellCenter(x, z);
                hits.Clear();
                map.Grid.Query(c.X - BodyRadius, c.Y - BodyRadius, c.X + BodyRadius, c.Y + BodyRadius, hits);
                foreach (int i in hits)
                {
                    Aabb b = map.Obstacles[i];
                    // Above step height and low enough to hit the body: a wall, not a kerb or a roof overhead.
                    if (b.Max.Y > GameRules.StepHeight && b.Min.Y < GameRules.PlayerHeight &&
                        b.Min.X < c.X + BodyRadius && b.Max.X > c.X - BodyRadius && b.Min.Z < c.Y + BodyRadius && b.Max.Z > c.Y - BodyRadius)
                    {
                        _blocked[z * _size + x] = true;
                        break;
                    }
                }
            }
        }

        public int Size => _size;

        private Vector2 CellCenter(int x, int z) => new Vector2(_origin + (x + 0.5f) * CellSize, _origin + (z + 0.5f) * CellSize);

        private bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < _size && z < _size;

        private (int X, int Z) CellOf(Vector3 p) =>
            ((int)Math.Floor((p.X - _origin) / CellSize), (int)Math.Floor((p.Z - _origin) / CellSize));

        public bool IsWalkable(Vector3 p)
        {
            var (x, z) = CellOf(p);
            return InBounds(x, z) && !_blocked[z * _size + x];
        }

        /// <summary>Nearest walkable cell centre within a few cells (for goals placed inside or next to walls).</summary>
        private bool Nearest(ref int x, ref int z)
        {
            if (InBounds(x, z) && !_blocked[z * _size + x]) return true;
            for (int r = 1; r <= 4; r++)
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                int nx = x + dx, nz = z + dz;
                if (InBounds(nx, nz) && !_blocked[nz * _size + nx])
                {
                    x = nx;
                    z = nz;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Waypoints from <paramref name="from"/> to <paramref name="to"/> (excluding the start), or null if unreachable.</summary>
        public List<Vector3>? FindPath(Vector3 from, Vector3 to)
        {
            var (sx, sz) = CellOf(from);
            var (gx, gz) = CellOf(to);
            if (!Nearest(ref sx, ref sz) || !Nearest(ref gx, ref gz)) return null;
            int start = sz * _size + sx, goal = gz * _size + gx;
            if (start == goal) return new List<Vector3> { to };

            var g = new Dictionary<int, float> { [start] = 0 };
            var parent = new Dictionary<int, int>();
            var open = new MinHeap();
            open.Push(start, Heuristic(sx, sz, gx, gz));
            int expansions = 0;
            while (open.Count > 0 && expansions++ < MaxExpansions)
            {
                int cur = open.Pop();
                if (cur == goal) return Smooth(Reconstruct(parent, cur), to);
                int cx = cur % _size, cz = cur / _size;
                float gc = g[cur];
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = cx + dx, nz = cz + dz;
                    if (!InBounds(nx, nz) || _blocked[nz * _size + nx]) continue;
                    if (dx != 0 && dz != 0 && (_blocked[cz * _size + nx] || _blocked[nz * _size + cx])) continue; // no corner cutting
                    int n = nz * _size + nx;
                    float cost = gc + (dx != 0 && dz != 0 ? 1.4142f : 1f);
                    if (g.TryGetValue(n, out float old) && old <= cost) continue;
                    g[n] = cost;
                    parent[n] = cur;
                    open.Push(n, cost + Heuristic(nx, nz, gx, gz));
                }
            }
            return null;
        }

        private static float Heuristic(int x, int z, int gx, int gz)
        {
            int dx = Math.Abs(x - gx), dz = Math.Abs(z - gz);
            return Math.Max(dx, dz) + 0.4142f * Math.Min(dx, dz);
        }

        private List<Vector3> Reconstruct(Dictionary<int, int> parent, int cur)
        {
            var cells = new List<Vector3>();
            while (parent.TryGetValue(cur, out int p))
            {
                Vector2 c = CellCenter(cur % _size, cur / _size);
                cells.Add(new Vector3(c.X, 0, c.Y));
                cur = p;
            }
            cells.Reverse();
            return cells;
        }

        /// <summary>Drops waypoints that can be skipped with a clear straight walk.</summary>
        private List<Vector3> Smooth(List<Vector3> cells, Vector3 goal)
        {
            if (cells.Count > 0) cells[cells.Count - 1] = new Vector3(goal.X, 0, goal.Z);
            var result = new List<Vector3>();
            int i = 0;
            while (i < cells.Count)
            {
                int j = cells.Count - 1;
                while (j > i + 1 && !ClearLine(result.Count > 0 ? result[result.Count - 1] : cells[i], cells[j])) j--;
                result.Add(cells[j]);
                i = j + 1;
            }
            return result;
        }

        /// <summary>True when every sample along the segment (every half cell) is walkable.</summary>
        public bool ClearLine(Vector3 a, Vector3 b)
        {
            float length = Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
            int steps = Math.Max(1, (int)(length / (CellSize * 0.5f)));
            for (int s = 1; s <= steps; s++)
                if (!IsWalkable(Vector3.Lerp(a, b, s / (float)steps))) return false;
            return true;
        }

        /// <summary>Binary min-heap of (cell, priority); netstandard2.1 has no PriorityQueue.</summary>
        private sealed class MinHeap
        {
            private readonly List<(int Item, float Priority)> _items = new List<(int, float)>();
            public int Count => _items.Count;

            public void Push(int item, float priority)
            {
                _items.Add((item, priority));
                int i = _items.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_items[p].Priority <= _items[i].Priority) break;
                    (_items[p], _items[i]) = (_items[i], _items[p]);
                    i = p;
                }
            }

            public int Pop()
            {
                int top = _items[0].Item;
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < _items.Count && _items[l].Priority < _items[m].Priority) m = l;
                    if (r < _items.Count && _items[r].Priority < _items[m].Priority) m = r;
                    if (m == i) break;
                    (_items[m], _items[i]) = (_items[i], _items[m]);
                    i = m;
                }
                return top;
            }
        }
    }
}
