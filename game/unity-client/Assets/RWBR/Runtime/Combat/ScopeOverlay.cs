using UnityEngine;

namespace RWBR.Client.Combat
{
    /// <summary>
    /// The scope view for 3x-and-above weapons (IMGUI, in the HUD's virtual units): black outside a round lens, a fine
    /// reticle, and a hit or down label. Shared by the practice and online HUDs.
    /// </summary>
    public static class ScopeOverlay
    {
        private static Texture2D _mask;

        public static void Draw(float width, float height, float sinceHit, bool kill)
        {
            if (_mask == null) _mask = Mask();
            float d = height * 0.92f;
            float x = width / 2 - d / 2, y = height / 2 - d / 2;
            GUI.DrawTexture(new Rect(x, y, d, d), _mask);
            Fill(new Rect(0, 0, x + 1, height), Color.black);
            Fill(new Rect(x + d - 1, 0, width - x - d + 1, height), Color.black);
            Color line = new Color(0, 0, 0, 0.85f);
            Fill(new Rect(x, height / 2 - 0.75f, d, 1.5f), line);
            Fill(new Rect(width / 2 - 0.75f, y, 1.5f, d), line);
            Fill(new Rect(width / 2 - 1.5f, height / 2 - 1.5f, 3, 3), new Color(0.9f, 0.15f, 0.1f));
            if (sinceHit < 0.15f) GUI.Label(new Rect(width / 2 + 12, height / 2 + 8, 120, 24), kill ? "DOWN" : "HIT");
        }

        private static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static Texture2D Mask()
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "ScopeMask", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                pixels[y * size + x] = new Color32(0, 0, 0, (byte)(Mathf.Clamp01((r - 0.96f) / 0.04f) * 255));
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }
}
