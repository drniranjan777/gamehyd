using RWBR.Client.Settings;
using UnityEngine;

namespace RWBR.Client.Accessibility
{
    /// <summary>
    /// Visual sound indicators (Phase 6 accessibility): recent gunshots the player could hear are drawn as arrows
    /// around the crosshair, pointing toward the shot, brighter when closer. Off unless enabled in settings.
    /// </summary>
    public static class SoundCues
    {
        private const int Capacity = 16;
        private const float ShowSeconds = 1.5f;
        private static readonly Vector3[] Positions = new Vector3[Capacity];
        private static readonly float[] Audible = new float[Capacity];
        private static readonly float[] Times = new float[Capacity];
        private static int _next;

        /// <summary>Records a sound at <paramref name="position"/> audible up to <paramref name="audibleMeters"/>.</summary>
        public static void Add(Vector3 position, float audibleMeters)
        {
            Positions[_next] = position;
            Audible[_next] = audibleMeters;
            Times[_next] = Time.time;
            _next = (_next + 1) % Capacity;
        }

        public static void Clear()
        {
            for (int i = 0; i < Capacity; i++) Times[i] = -100f;
        }

        /// <summary>Screen direction (radians, 0 = straight ahead, clockwise) and strength 0..1 of a cue, if it should show.</summary>
        public static bool TryGet(int i, Transform listener, out float angle, out float strength)
        {
            angle = strength = 0;
            float age = Time.time - Times[i];
            if (age < 0 || age > ShowSeconds || listener == null) return false;
            Vector3 to = Positions[i] - listener.position;
            float distance = to.magnitude;
            if (distance > Audible[i] || distance < 0.5f) return false;
            Vector3 local = listener.InverseTransformDirection(to);
            angle = Mathf.Atan2(local.x, local.z);
            strength = (1f - distance / Audible[i]) * (1f - age / ShowSeconds);
            return true;
        }

        /// <summary>Draws the cues in the HUD's virtual coordinates (width <paramref name="w"/>, height <paramref name="h"/>).</summary>
        public static void Draw(float w, float h, Camera cam)
        {
            if (!GameSettings.VisualSoundCues || cam == null) return;
            Vector2 centre = new Vector2(w / 2, h / 2);
            const float radius = 110f;
            for (int i = 0; i < Capacity; i++)
            {
                if (!TryGet(i, cam.transform, out float angle, out float strength)) continue;
                Vector2 at = centre + new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)) * radius;
                Matrix4x4 m = GUI.matrix;
                Vector2 pivot = GUIUtility.GUIToScreenPoint(at);
                GUIUtility.RotateAroundPivot(angle * Mathf.Rad2Deg, pivot);
                Color old = GUI.color;
                GUI.color = new Color(1f, 0.85f, 0.2f, 0.35f + 0.65f * strength);
                GUI.DrawTexture(new Rect(at.x - 14, at.y - 4, 28, 8), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(at.x - 4, at.y - 14, 8, 10), Texture2D.whiteTexture);
                GUI.color = old;
                GUI.matrix = m;
            }
        }
    }
}
