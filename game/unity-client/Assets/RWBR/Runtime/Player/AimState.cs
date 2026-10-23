using UnityEngine;

namespace RWBR.Client.Player
{
    /// <summary>
    /// Client-side aim feel shared by practice and online play: the smoothed hip-to-aim blend that drives the camera
    /// zoom, and view recoil. Each shot climbs the view a little for good and kicks it further for a moment; the kick
    /// recovers on its own, so controlled bursts stay on target. Shots follow the kicked view, as the crosshair does.
    /// Whether the player is aiming is also sent to the server (InputButtons.Aim), which decides spread.
    /// </summary>
    public sealed class AimState
    {
        /// <summary>Seconds from hip to fully aimed (and back).</summary>
        public const float BlendSeconds = 0.18f;
        /// <summary>Share of a weapon's recoil that stays in the view after the kick recovers.</summary>
        public const float ClimbShare = 0.45f;
        /// <summary>Share of a weapon's recoil applied as a kick that recovers.</summary>
        public const float KickShare = 0.8f;
        /// <summary>Recoil multiplier while aiming (braced weapon).</summary>
        public const float AimedRecoil = 0.75f;
        /// <summary>Kick recovery rate (per second, exponential).</summary>
        public const float RecoverRate = 9f;
        /// <summary>Zoom at which the camera moves to the eye and the scope overlay is drawn.</summary>
        public const float ScopeZoom = 3f;

        private int _recoilSign = 1;

        /// <summary>The player is holding (or has toggled) aim this frame.</summary>
        public bool Aiming { get; private set; }
        /// <summary>0 = hip fire, 1 = fully aimed. Eased for the camera with <see cref="Eased"/>.</summary>
        public float Blend { get; private set; }
        /// <summary>The weapon in hand's aim zoom (1 when unarmed).</summary>
        public float Zoom { get; private set; } = 1f;
        public float KickPitch { get; private set; }
        public float KickYaw { get; private set; }

        public float Eased => Blend * Blend * (3f - 2f * Blend);
        /// <summary>Field of view is divided by this.</summary>
        public float FovDivisor => Mathf.Lerp(1f, Zoom, Eased);
        public bool UsesScope => Zoom >= ScopeZoom;
        public bool Scoped => UsesScope && Blend > 0.85f;

        public void Update(bool aiming, float zoom, float dt)
        {
            Aiming = aiming;
            Zoom = Mathf.Max(1f, zoom);
            Blend = Mathf.MoveTowards(Blend, aiming ? 1f : 0f, dt / BlendSeconds);
            float keep = Mathf.Exp(-RecoverRate * dt);
            KickPitch *= keep;
            KickYaw *= keep;
        }

        public void Reset()
        {
            Aiming = false;
            Blend = 0;
            KickPitch = KickYaw = 0;
        }

        /// <summary>
        /// Applies one trigger pull's recoil. Returns the permanent (pitch, yaw) climb in degrees for the caller to add
        /// to its view angles; the recovering kick is kept here. Pitch is positive down, so recoil is negative.
        /// </summary>
        public Vector2 Recoil(float pitchDegrees, float yawDegrees)
        {
            float scale = Aiming ? AimedRecoil : 1f;
            float yaw = yawDegrees * (_recoilSign = -_recoilSign) * scale;
            KickPitch -= pitchDegrees * KickShare * scale;
            KickYaw += yaw * KickShare;
            return new Vector2(-pitchDegrees * ClimbShare * scale, yaw * ClimbShare);
        }

        /// <summary>Look sensitivity multiplier: zoomed views turn slower in proportion to the zoom.</summary>
        public float SensitivityScale(float aimSensitivity) => Mathf.Lerp(1f, aimSensitivity / Mathf.Max(1f, Zoom), Eased);
    }
}
