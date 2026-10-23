using RWBR.Client.Settings;
using UnityEngine;

namespace RWBR.Client.Character
{
    /// <summary>How much work a character gets this frame, by distance from the camera.</summary>
    public enum DetailTier
    {
        /// <summary>Everything: two-bone skinning, shadows, hand, head and foot IK.</summary>
        Near = 0,
        /// <summary>No foot IK (its raycasts cost the most and planted feet can't be seen at this range).</summary>
        Mid = 1,
        /// <summary>One-bone skinning, no shadow, no IK.</summary>
        Far = 2,
    }

    /// <summary>
    /// Distance-based detail for characters, standing in for mesh LODs until a character pack with LODs is in. The
    /// camera reports where it is each frame; each body picks its tier from that, with a little hysteresis so a
    /// character standing on a boundary does not flicker between tiers. Lower quality presets switch closer.
    /// </summary>
    public static class CharacterDetail
    {
        public const float NearDistance = 15f;
        public const float FarDistance = 45f;
        public const float Hysteresis = 2f;

        /// <summary>The camera position last frame (set by the third-person camera); null until a camera reports.</summary>
        public static Vector3? Viewer { get; set; }

        public static float DistanceScale(QualityPreset quality)
        {
            switch (quality)
            {
                case QualityPreset.Low: return 0.6f;
                case QualityPreset.High: return 1.5f;
                default: return 1f;
            }
        }

        /// <summary>The tier for a character at <paramref name="distance"/> metres that is currently at <paramref name="current"/>.</summary>
        public static DetailTier Tier(float distance, DetailTier current, QualityPreset quality)
        {
            float scale = DistanceScale(quality);
            float near = NearDistance * scale, far = FarDistance * scale;
            // Move out a tier only past the boundary plus the margin, and back in only inside it minus the margin.
            float nearEdge = current == DetailTier.Near ? near + Hysteresis : near - Hysteresis;
            float farEdge = current == DetailTier.Far ? far - Hysteresis : far + Hysteresis;
            if (distance >= farEdge) return DetailTier.Far;
            if (distance >= nearEdge) return DetailTier.Mid;
            return DetailTier.Near;
        }
    }
}
