using RWBR.Shared;
using UnityEngine;

namespace RWBR.Client.Greybox
{
    /// <summary>
    /// Builds the Phase 0 greybox from <see cref="MapLayout.GreyboxBlock"/>, the same layout the match server
    /// uses for collision and line of sight, so client and server geometry cannot drift apart.
    /// Generated blocks are never saved into the scene; they are rebuilt whenever the scene loads.
    /// </summary>
    [ExecuteAlways]
    public sealed class GreyboxWorld : MonoBehaviour
    {
        [SerializeField] private float halfSize = 100f;

        private MapLayout _layout;

        public MapLayout Layout => _layout ??= MapLayout.GreyboxBlock(halfSize);

        /// <summary>
        /// Rebuilds for a server map version such as "greybox-block-40-v2". Returns false for unknown versions,
        /// in which case the client must not play (its collision would not match the server's).
        /// </summary>
        public bool MatchServerMap(string version)
        {
            if (version == Layout.Version) return true;
            string[] parts = version.Split('-');
            if (parts.Length < 3 || parts[0] != "greybox" || parts[1] != "block" ||
                !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float size))
                return false;
            MapLayout candidate = MapLayout.GreyboxBlock(size);
            if (candidate.Version != version) return false;
            halfSize = size;
            _layout = candidate;
            Build();
            return true;
        }

        private void Awake()
        {
            if (transform.childCount == 0) Build();
        }

        public void Build()
        {
            for (int i = transform.childCount - 1; i >= 0; i--) DestroyImmediate(transform.GetChild(i).gameObject);

            MapLayout layout = Layout;
            float size = layout.HalfSize * 2;

            // Ground and two crossing roads (visual only; the server map is flat).
            CreateBlock("Ground", new Vector3(0, -0.05f, 0), new Vector3(size, 0.1f, size), new Color(0.45f, 0.42f, 0.36f), collider: true);
            CreateBlock("Road_NS", new Vector3(-20, 0.005f, 0), new Vector3(8, 0.01f, size), new Color(0.18f, 0.18f, 0.2f), collider: false);
            CreateBlock("Road_EW", new Vector3(0, 0.005f, -20), new Vector3(size, 0.01f, 8), new Color(0.18f, 0.18f, 0.2f), collider: false);

            for (int i = 0; i < layout.Obstacles.Count; i++)
            {
                Aabb box = layout.Obstacles[i];
                var min = new Vector3(box.Min.X, box.Min.Y, box.Min.Z);
                var max = new Vector3(box.Max.X, box.Max.Y, box.Max.Z);
                CreateBlock($"Obstacle_{i:00}", (min + max) / 2, max - min, ColorFor(max.y - min.y, min.y), collider: true);
            }
            Physics.SyncTransforms(); // make the new colliders visible to queries immediately
        }

        private GameObject CreateBlock(string name, Vector3 center, Vector3 size, Color color, bool collider)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = center;
            go.transform.localScale = size;
            if (!collider) DestroyImmediate(go.GetComponent<Collider>());

            var renderer = go.GetComponent<MeshRenderer>();
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", color);
            block.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(block);
            return go;
        }

        private static Color ColorFor(float height, float baseY)
        {
            if (baseY > 0.5f) return new Color(0.55f, 0.5f, 0.45f);  // roof slab
            if (height >= 10f) return new Color(0.82f, 0.74f, 0.6f);  // apartment blocks
            if (height >= 3f) return new Color(0.9f, 0.88f, 0.82f);   // building walls
            if (height >= 2f) return new Color(0.85f, 0.45f, 0.2f);   // market stalls
            return new Color(0.6f, 0.6f, 0.6f);                       // low cover
        }
    }
}
