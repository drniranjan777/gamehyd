using System.Collections.Generic;
using RWBR.Shared;
using UnityEngine;

namespace RWBR.Client.Online
{
    /// <summary>
    /// Minimap, compass and the full map, drawn with IMGUI from a top-down picture of the map built from the same
    /// <see cref="MapLayout"/> the server uses (so it can never disagree with the world). The minimap is north-up and
    /// shows about 150 m around you; the full map shows everything, with district names, and places your marker.
    /// </summary>
    public sealed class MapHud
    {
        public const float MinimapMetres = 150f;
        private const int MaxTexture = 1024;

        private static readonly Dictionary<string, Texture2D> Pictures = new Dictionary<string, Texture2D>();
        // Read on use: Unity forbids touching textures while a MonoBehaviour (the HUD that owns this) is constructed.
        private static Texture2D White => Texture2D.whiteTexture;

        /// <summary>Top-down picture of a map version (built once, then cached): ground, roads, buildings by kind, water.</summary>
        public static Texture2D Picture(MapLayout map)
        {
            if (Pictures.TryGetValue(map.Version, out Texture2D cached) && cached != null) return cached;
            int size = Mathf.Clamp(Mathf.NextPowerOfTwo((int)(map.HalfSize * 2)), 256, MaxTexture);
            float metresPerPixel = map.HalfSize * 2 / size;
            var px = new Color32[size * size];
            var ground = new Color32(170, 160, 135, 255);
            for (int i = 0; i < px.Length; i++) px[i] = ground;

            void Fill(float minX, float minZ, float maxX, float maxZ, Color32 c)
            {
                int x0 = Mathf.Clamp((int)((minX + map.HalfSize) / metresPerPixel), 0, size - 1);
                int x1 = Mathf.Clamp((int)((maxX + map.HalfSize) / metresPerPixel), 0, size - 1);
                int z0 = Mathf.Clamp((int)((minZ + map.HalfSize) / metresPerPixel), 0, size - 1);
                int z1 = Mathf.Clamp((int)((maxZ + map.HalfSize) / metresPerPixel), 0, size - 1);
                for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    px[z * size + x] = c;
            }

            if (map.HalfSize > CityMap.HalfSize && map.Version.StartsWith("hyd-city", System.StringComparison.Ordinal))
            {
                float h = map.HalfSize, c = CityMap.HalfSize;
                Fill(-h, c, h, h, new Color32(150, 150, 150, 255));
                Fill(-h, -c, -c, c, new Color32(150, 150, 150, 255));
                Fill(-h, -h, h, -c, new Color32(120, 150, 90, 255));
                Fill(CityMap.RiverEast, -c, h, c, new Color32(120, 150, 90, 255));
            }
            if (map.Version.StartsWith("hyd-city", System.StringComparison.Ordinal))
            {
                Fill(-10, -map.HalfSize, 10, map.HalfSize, new Color32(70, 70, 75, 255));
                Fill(-map.HalfSize, -10, map.HalfSize, 10, new Color32(70, 70, 75, 255));
            }
            // Low things first, tall things last, so towers and roofs read on top.
            var order = new List<int>();
            for (int i = 0; i < map.Obstacles.Count; i++) order.Add(i);
            order.Sort((a, b) => map.Obstacles[a].Max.Y.CompareTo(map.Obstacles[b].Max.Y));
            foreach (int i in order)
            {
                Aabb b = map.Obstacles[i];
                if (b.Min.Y > 6f && map.KindOf(i) != BoxKind.Viaduct) continue; // arches and canopies: show what's below
                Fill(b.Min.X, b.Min.Z, b.Max.X, b.Max.Z, ColourOf(map.KindOf(i), b.Max.Y));
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Map_" + map.Version, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(false, true); // no longer readable: frees the CPU copy
            Pictures[map.Version] = tex;
            return tex;
        }

        private static Color32 ColourOf(BoxKind kind, float height)
        {
            switch (kind)
            {
                case BoxKind.Water: return new Color32(60, 115, 160, 255);
                case BoxKind.Tree: return new Color32(70, 120, 70, 255);
                case BoxKind.Field: return new Color32(200, 180, 100, 255);
                case BoxKind.Rock: return new Color32(120, 115, 105, 255);
                case BoxKind.Bridge:
                case BoxKind.Viaduct: return new Color32(95, 95, 100, 255);
                case BoxKind.Container: return new Color32(150, 80, 60, 255);
                case BoxKind.Car: return new Color32(150, 50, 50, 255);
                case BoxKind.Stall: return new Color32(200, 120, 60, 255);
                case BoxKind.Cover:
                case BoxKind.CompoundWall:
                case BoxKind.Railing: return new Color32(140, 135, 125, 255);
            }
            // Buildings: darker the taller.
            byte v = (byte)Mathf.Clamp(215 - height * 1.6f, 90, 215);
            return new Color32(v, (byte)(v - 8), (byte)(v - 20), 255);
        }

        /// <summary>
        /// The minimap at <paramref name="r"/>, north-up and centred on you, with the zone and next circle, your heading
        /// and your marker.
        /// </summary>
        public void DrawMinimap(Rect r, MapLayout map, Vector2 me, float yawDegrees, ZoneView zone, Vector2? marker,
            IReadOnlyList<Vector2> teammates = null)
        {
            Texture2D pic = Picture(map);
            float size = map.HalfSize * 2;
            float half = MinimapMetres;
            // The texture's v runs south to north; the screen's y runs top to bottom.
            var uv = new Rect((me.x - half + map.HalfSize) / size, (me.y - half + map.HalfSize) / size, half * 2 / size, half * 2 / size);
            Fill(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), new Color(0, 0, 0, 0.6f));
            GUI.DrawTextureWithTexCoords(r, pic, uv);
            Vector2 ToScreen(Vector2 world) => new Vector2(r.center.x + (world.x - me.x) / half * r.width / 2, r.center.y - (world.y - me.y) / half * r.height / 2);
            GUI.BeginGroup(r);
            var local = new Rect(0, 0, r.width, r.height);
            Vector2 Local(Vector2 w) => ToScreen(w) - r.position;
            Circle(local, Local(zone.Center), zone.Radius / half * r.width / 2, new Color(0.3f, 0.6f, 1f, 0.9f), 2f);
            if (zone.Closing) Circle(local, Local(zone.NextCenter), zone.NextRadius / half * r.width / 2, Color.white, 1.5f);
            if (marker.HasValue) Dot(Local(marker.Value), 7, new Color(1f, 0.85f, 0.1f));
            if (teammates != null)
                foreach (Vector2 t in teammates)
                {
                    Vector2 q = Local(t);
                    if (local.Contains(q)) Dot(q, 7, new Color(0.2f, 0.85f, 0.45f));
                }
            GUI.EndGroup();
            Arrow(r.center, yawDegrees, 9, new Color(1f, 1f, 1f));
        }

        /// <summary>The compass strip: bearings, your marker and, when you're outside the next circle, the way to it.</summary>
        public void DrawCompass(Rect r, float yawDegrees, Vector2 me, Vector2? marker, ZoneView zone, GUIStyle small)
        {
            Fill(r, new Color(0, 0, 0, 0.35f));
            const float span = 120f; // degrees across the strip
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            for (int deg = 0; deg < 360; deg += 15)
            {
                float d = Mathf.DeltaAngle(yawDegrees, deg);
                if (Mathf.Abs(d) > span / 2) continue;
                float x = r.center.x + d / span * r.width;
                bool major = deg % 45 == 0;
                Fill(new Rect(x - 1, r.yMax - (major ? 12 : 7), 2, major ? 12 : 7), new Color(1, 1, 1, 0.8f));
                if (major) GUI.Label(new Rect(x - 20, r.y, 40, 18), names[deg / 45], new GUIStyle(small) { alignment = TextAnchor.UpperCenter });
            }
            Fill(new Rect(r.center.x - 1, r.y - 4, 2, r.height + 6), new Color(1f, 0.85f, 0.2f));
            GUI.Label(new Rect(r.center.x - 30, r.yMax + 2, 60, 18), $"{Mathf.RoundToInt(Mathf.Repeat(yawDegrees, 360f)):000}°", new GUIStyle(small) { alignment = TextAnchor.UpperCenter });

            void Bearing(Vector2 target, Color c, string label)
            {
                Vector2 d = target - me;
                float bearing = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
                float off = Mathf.Clamp(Mathf.DeltaAngle(yawDegrees, bearing), -span / 2, span / 2);
                float x = r.center.x + off / span * r.width;
                Fill(new Rect(x - 3, r.y + 2, 6, r.height - 4), c);
                GUI.Label(new Rect(x - 50, r.yMax + 18, 100, 18), $"{label} {d.magnitude:0} m", new GUIStyle(small) { alignment = TextAnchor.UpperCenter });
            }
            if (marker.HasValue) Bearing(marker.Value, new Color(1f, 0.85f, 0.1f, 0.9f), "Marker");
            if (zone.Closing && Vector2.Distance(me, zone.NextCenter) > zone.NextRadius)
                Bearing(zone.NextCenter, new Color(1f, 1f, 1f, 0.8f), "Zone");
        }

        /// <summary>
        /// The full map filling <paramref name="r"/> (square), with district names, the zone, you and your marker. Returns the
        /// world point clicked, if any (the caller sets or clears the marker).
        /// </summary>
        public Vector2? DrawFullMap(Rect r, MapLayout map, Vector2 me, float yawDegrees, ZoneView zone, Vector2? marker, GUIStyle label)
        {
            float size = map.HalfSize * 2;
            Fill(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6), new Color(0, 0, 0, 0.8f));
            GUI.DrawTexture(r, Picture(map));
            Vector2 ToScreen(Vector2 w) => new Vector2(r.x + (w.x + map.HalfSize) / size * r.width, r.yMax - (w.y + map.HalfSize) / size * r.height);
            foreach (District d in map.Districts)
            {
                Vector2 c = ToScreen(new Vector2((d.MinX + d.MaxX) / 2, (d.MinZ + d.MaxZ) / 2));
                GUI.Label(new Rect(c.x - 90, c.y - 12, 180, 24), d.Name, new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, fontSize = 14 });
            }
            GUI.BeginGroup(r);
            Vector2 Local(Vector2 w) => ToScreen(w) - r.position;
            float scale = r.width / size;
            Circle(new Rect(0, 0, r.width, r.height), Local(zone.Center), zone.Radius * scale, new Color(0.3f, 0.6f, 1f, 0.9f), 2f);
            if (zone.Closing) Circle(new Rect(0, 0, r.width, r.height), Local(zone.NextCenter), zone.NextRadius * scale, Color.white, 1.5f);
            if (marker.HasValue) Dot(Local(marker.Value), 10, new Color(1f, 0.85f, 0.1f));
            GUI.EndGroup();
            Arrow(ToScreen(me), yawDegrees, 11, Color.white);

            Event e = Event.current;
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                e.Use();
                return new Vector2((e.mousePosition.x - r.x) / r.width * size - map.HalfSize, (r.yMax - e.mousePosition.y) / r.height * size - map.HalfSize);
            }
            return null;
        }

        public struct ZoneView
        {
            public Vector2 Center;
            public float Radius;
            public Vector2 NextCenter;
            public float NextRadius;
            public bool Closing => NextRadius < Radius - 0.5f;
        }

        private void Circle(Rect clip, Vector2 c, float radius, Color colour, float thickness)
        {
            if (radius <= 0.5f) return;
            int steps = Mathf.Clamp((int)(radius * 0.6f), 24, 160);
            for (int i = 0; i < steps; i++)
            {
                float a = i * Mathf.PI * 2 / steps;
                var p = new Vector2(c.x + Mathf.Sin(a) * radius, c.y - Mathf.Cos(a) * radius);
                if (!clip.Contains(p)) continue;
                Fill(new Rect(p.x - thickness / 2, p.y - thickness / 2, thickness, thickness), colour);
            }
        }

        private void Dot(Vector2 p, float size, Color colour)
        {
            Fill(new Rect(p.x - size / 2 - 1, p.y - size / 2 - 1, size + 2, size + 2), Color.black);
            Fill(new Rect(p.x - size / 2, p.y - size / 2, size, size), colour);
        }

        /// <summary>You: a dot with a short line pointing where you look.</summary>
        private void Arrow(Vector2 p, float yawDegrees, float size, Color colour)
        {
            Dot(p, size * 0.8f, colour);
            float a = yawDegrees * Mathf.Deg2Rad;
            for (int i = 1; i <= 4; i++)
            {
                var q = new Vector2(p.x + Mathf.Sin(a) * size * i * 0.45f, p.y - Mathf.Cos(a) * size * i * 0.45f);
                Fill(new Rect(q.x - 1.5f, q.y - 1.5f, 3, 3), colour);
            }
        }

        private void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, White);
            GUI.color = old;
        }
    }
}
