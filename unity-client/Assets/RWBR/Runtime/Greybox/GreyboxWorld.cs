using System.Collections.Generic;
using RWBR.Shared;
using UnityEngine;
using UnityEngine.Rendering;

namespace RWBR.Client.Greybox
{
    /// <summary>
    /// Builds the world for a map version from the same <see cref="MapLayout"/> the match server uses, so client and
    /// server geometry cannot drift. Boxes are merged into 64 m chunk meshes (vertex-coloured by box kind) with a LOD
    /// group per chunk: full detail near, tall silhouettes far, culled beyond. Generated objects are never saved into
    /// the scene; they are rebuilt whenever the scene loads.
    /// </summary>
    [ExecuteAlways]
    public sealed class GreyboxWorld : MonoBehaviour
    {
        public const float ChunkSize = 64f;
        /// <summary>Boxes at least this tall (or of skyline kinds) stay visible as far silhouettes.</summary>
        private const float SilhouetteHeight = 6f;

        [SerializeField] private string mapVersion = "greybox-block-100-v2";

        private MapLayout _layout;
        private Material _material;

        public MapLayout Layout => _layout ??= Resolve(mapVersion);
        public int ChunkCount { get; private set; }
        public int VertexCount { get; private set; }

        private static MapLayout Resolve(string version) =>
            MapLayout.TryFromVersion(version, out MapLayout layout) ? layout : MapLayout.GreyboxBlock(100f);

        /// <summary>
        /// Rebuilds for a server map version ("greybox-block-40-v2", "hyd-city-v1"). Returns false for unknown
        /// versions, in which case the client must not play (its collision would not match the server's).
        /// </summary>
        public bool MatchServerMap(string version)
        {
            if (version == Layout.Version) return true;
            if (!MapLayout.TryFromVersion(version, out MapLayout candidate)) return false;
            mapVersion = version;
            _layout = candidate;
            Build();
            return true;
        }

        /// <summary>Switch to another map version (practice map choice, tests).</summary>
        public void UseMap(string version)
        {
            if (!MatchServerMap(version)) throw new System.ArgumentException($"Unknown map version {version}");
        }

        private void Awake()
        {
            if (transform.childCount == 0) Build();
        }

        public void Build()
        {
            for (int i = transform.childCount - 1; i >= 0; i--) DestroyImmediate(transform.GetChild(i).gameObject);
            if (_material == null)
            {
                Shader shader = Shader.Find("RWBR/VertexColorLit") ?? Shader.Find("Standard");
                _material = new Material(shader) { name = "GreyboxWorld", hideFlags = HideFlags.DontSave };
            }

            MapLayout layout = Layout;
            float size = layout.HalfSize * 2;
            VertexCount = 0;

            // Ground, with the city's avenues as darker strips (visual only; the ground is flat for the server).
            var ground = new MeshBuilder();
            ground.Box(new Vector3(-layout.HalfSize, -0.1f, -layout.HalfSize), new Vector3(layout.HalfSize, 0, layout.HalfSize), new Color(0.45f, 0.42f, 0.36f));
            if (layout.Version == CityMap.Version)
            {
                var road = new Color(0.2f, 0.2f, 0.22f);
                ground.Box(new Vector3(-10, 0, -layout.HalfSize), new Vector3(10, 0.01f, layout.HalfSize), road);
                ground.Box(new Vector3(-layout.HalfSize, 0, -10), new Vector3(layout.HalfSize, 0.01f, 10), road);
            }
            else
            {
                var road = new Color(0.18f, 0.18f, 0.2f);
                ground.Box(new Vector3(-24, 0, -layout.HalfSize), new Vector3(-16, 0.01f, layout.HalfSize), road);
                ground.Box(new Vector3(-layout.HalfSize, 0, -24), new Vector3(layout.HalfSize, 0.01f, -16), road);
            }
            AddMeshObject("Ground", ground.ToMesh("Ground"), transform, collider: true);

            // Bucket boxes into chunks by centre.
            int n = Mathf.CeilToInt(size / ChunkSize);
            var chunks = new Dictionary<int, List<int>>();
            for (int i = 0; i < layout.Obstacles.Count; i++)
            {
                Aabb b = layout.Obstacles[i];
                int cx = Mathf.Clamp(Mathf.FloorToInt(((b.Min.X + b.Max.X) / 2 + layout.HalfSize) / ChunkSize), 0, n - 1);
                int cz = Mathf.Clamp(Mathf.FloorToInt(((b.Min.Z + b.Max.Z) / 2 + layout.HalfSize) / ChunkSize), 0, n - 1);
                int key = cz * n + cx;
                if (!chunks.TryGetValue(key, out List<int> list)) chunks[key] = list = new List<int>();
                list.Add(i);
            }

            ChunkCount = 0;
            foreach (KeyValuePair<int, List<int>> kv in chunks)
            {
                var near = new MeshBuilder();
                var far = new MeshBuilder();
                foreach (int i in kv.Value)
                {
                    Aabb b = layout.Obstacles[i];
                    BoxKind kind = layout.KindOf(i);
                    Vector3 min = new Vector3(b.Min.X, b.Min.Y, b.Min.Z), max = new Vector3(b.Max.X, b.Max.Y, b.Max.Z);
                    if (kind == BoxKind.Water) max.y = 0.05f; // the 1.3 m collision barrier renders as a flat surface
                    Color c = ColorFor(kind, i);
                    near.Box(min, max, c);
                    if (max.y - min.y >= SilhouetteHeight || kind == BoxKind.Viaduct || kind == BoxKind.Landmark || kind == BoxKind.Water)
                        far.Box(min, max, c);
                }

                var chunk = new GameObject($"Chunk_{kv.Key % n}_{kv.Key / n}") { hideFlags = HideFlags.DontSave };
                chunk.transform.SetParent(transform, false);
                Renderer nearRenderer = AddMeshObject("LOD0", near.ToMesh($"Chunk{kv.Key}_LOD0"), chunk.transform, collider: true);
                var lods = new List<LOD> { new LOD(0.12f, new[] { nearRenderer }) };
                if (far.VertexCount > 0)
                {
                    Renderer farRenderer = AddMeshObject("LOD1", far.ToMesh($"Chunk{kv.Key}_LOD1"), chunk.transform, collider: false);
                    farRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    lods.Add(new LOD(0.01f, new[] { farRenderer }));
                }
                LODGroup group = chunk.AddComponent<LODGroup>();
                group.SetLODs(lods.ToArray());
                group.RecalculateBounds();
                ChunkCount++;
                VertexCount += near.VertexCount;
            }
            Physics.SyncTransforms(); // make the new colliders visible to queries immediately
        }

        private Renderer AddMeshObject(string name, Mesh mesh, Transform parent, bool collider)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            if (collider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
            return renderer;
        }

        private static Color ColorFor(BoxKind kind, int index)
        {
            Color c;
            switch (kind)
            {
                case BoxKind.Roof: c = new Color(0.55f, 0.5f, 0.45f); break;
                case BoxKind.Stair: c = new Color(0.7f, 0.68f, 0.62f); break;
                case BoxKind.Cover: c = new Color(0.6f, 0.6f, 0.6f); break;
                case BoxKind.Stall: c = new Color(0.85f, 0.45f, 0.2f); break;
                case BoxKind.Shop: c = new Color(0.93f, 0.82f, 0.62f); break;
                case BoxKind.House: c = new Color(0.9f, 0.88f, 0.82f); break;
                case BoxKind.Office: c = new Color(0.55f, 0.65f, 0.75f); break;
                case BoxKind.Landmark: c = new Color(0.85f, 0.75f, 0.55f); break;
                case BoxKind.Water: c = new Color(0.2f, 0.45f, 0.6f); break;
                case BoxKind.Viaduct: c = new Color(0.6f, 0.6f, 0.65f); break;
                case BoxKind.Pillar: c = new Color(0.55f, 0.55f, 0.6f); break;
                case BoxKind.Tree: c = new Color(0.25f, 0.5f, 0.25f); break;
                case BoxKind.Car: c = new Color(0.7f, 0.15f, 0.15f); break;
                case BoxKind.CompoundWall: c = new Color(0.75f, 0.7f, 0.6f); break;
                case BoxKind.Wall: c = new Color(0.9f, 0.88f, 0.82f); break;
                default: c = new Color(0.8f, 0.74f, 0.6f); break;
            }
            // Small deterministic variation so neighbouring buildings read as separate.
            float v = 0.92f + ((index * 2654435761u) >> 28) / 15f * 0.16f;
            return new Color(c.r * v, c.g * v, c.b * v);
        }

        /// <summary>Accumulates boxes (6 faces, 24 vertices each) into one mesh.</summary>
        private sealed class MeshBuilder
        {
            private readonly List<Vector3> _v = new List<Vector3>();
            private readonly List<Vector3> _n = new List<Vector3>();
            private readonly List<Color> _c = new List<Color>();
            private readonly List<int> _t = new List<int>();

            public int VertexCount => _v.Count;

            public void Box(Vector3 min, Vector3 max, Color color)
            {
                Face(new Vector3(min.x, max.y, min.z), new Vector3(min.x, max.y, max.z), new Vector3(max.x, max.y, max.z), new Vector3(max.x, max.y, min.z), Vector3.up, color);
                Face(new Vector3(min.x, min.y, max.z), new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), Vector3.down, color);
                Face(new Vector3(min.x, min.y, min.z), new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, min.y, min.z), Vector3.back, color);
                Face(new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z), new Vector3(min.x, min.y, max.z), Vector3.forward, color);
                Face(new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, max.z), new Vector3(min.x, max.y, min.z), new Vector3(min.x, min.y, min.z), Vector3.left, color);
                Face(new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z), new Vector3(max.x, min.y, max.z), Vector3.right, color);
            }

            private void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color color)
            {
                int i = _v.Count;
                _v.Add(a);
                _v.Add(b);
                _v.Add(c);
                _v.Add(d);
                for (int k = 0; k < 4; k++)
                {
                    _n.Add(normal);
                    _c.Add(color);
                }
                _t.Add(i);
                _t.Add(i + 1);
                _t.Add(i + 2);
                _t.Add(i);
                _t.Add(i + 2);
                _t.Add(i + 3);
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
                if (_v.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(_v);
                mesh.SetNormals(_n);
                mesh.SetColors(_c);
                mesh.SetTriangles(_t, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
