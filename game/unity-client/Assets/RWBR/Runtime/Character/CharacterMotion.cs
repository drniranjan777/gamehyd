using RWBR.Shared;
using UnityEngine;

namespace RWBR.Client.Character
{
    /// <summary>What a character is doing this frame, from the simulation (practice), prediction (you, online) or
    /// interpolated snapshots (others, online). Everything a body needs to pose itself.</summary>
    public struct CharacterMotion
    {
        /// <summary>World velocity in m/s (horizontal and vertical).</summary>
        public Vector3 Velocity;
        /// <summary>Where the character is looking (degrees). The rigged body turns towards it.</summary>
        public float ViewYawDegrees;
        /// <summary>View pitch in degrees, positive down.</summary>
        public float PitchDegrees;
        public bool Grounded;
        public bool Crouched;
        public bool Alive;
        /// <summary>0 = hip, 1 = aimed.</summary>
        public float Aim;
        public bool Reloading;
        /// <summary>How far through the reload (0..1); drives the reload clip so it ends when the reload does.</summary>
        public float ReloadProgress;
        /// <summary>The weapon in hand, or null when unarmed.</summary>
        public WeaponClass? Weapon;
    }

    /// <summary>Animator parameter names and hashes shared by the controller builder and the runtime driver.</summary>
    public static class AnimatorIds
    {
        public const string ForwardName = "Forward";
        public const string StrafeName = "Strafe";
        public const string SpeedName = "Speed";
        public const string CrouchName = "Crouch";
        public const string GroundedName = "Grounded";
        public const string AimName = "Aim";
        public const string PitchName = "Pitch";
        public const string PistolName = "Pistol";
        public const string ArmedName = "Armed";
        public const string ReloadingName = "Reloading";
        public const string ReloadProgressName = "ReloadProgress";
        public const string DeadName = "Dead";
        public const string JumpName = "Jump";
        public const string LandName = "Land";
        public const string HitName = "Hit";
        public const string FireName = "Fire";
        public const string SwitchName = "Switch";
        public const string TurnName = "Turn";
        public const string TurnAngleName = "TurnAngle";

        public static readonly int Forward = Animator.StringToHash(ForwardName);
        public static readonly int Strafe = Animator.StringToHash(StrafeName);
        public static readonly int Speed = Animator.StringToHash(SpeedName);
        public static readonly int Crouch = Animator.StringToHash(CrouchName);
        public static readonly int Grounded = Animator.StringToHash(GroundedName);
        public static readonly int Aim = Animator.StringToHash(AimName);
        public static readonly int Pitch = Animator.StringToHash(PitchName);
        public static readonly int Pistol = Animator.StringToHash(PistolName);
        public static readonly int Armed = Animator.StringToHash(ArmedName);
        public static readonly int Reloading = Animator.StringToHash(ReloadingName);
        public static readonly int ReloadProgress = Animator.StringToHash(ReloadProgressName);
        public static readonly int Dead = Animator.StringToHash(DeadName);
        public static readonly int Jump = Animator.StringToHash(JumpName);
        public static readonly int Land = Animator.StringToHash(LandName);
        public static readonly int Hit = Animator.StringToHash(HitName);
        public static readonly int Fire = Animator.StringToHash(FireName);
        public static readonly int Switch = Animator.StringToHash(SwitchName);
        public static readonly int Turn = Animator.StringToHash(TurnName);
        public static readonly int TurnAngle = Animator.StringToHash(TurnAngleName);
    }

    public static class LocomotionMath
    {
        /// <summary>Below this horizontal speed (m/s) the character counts as standing still.</summary>
        public const float StillSpeed = 0.3f;

        /// <summary>Horizontal velocity in the body's frame: x = strafe right, y = forward (m/s).</summary>
        public static Vector2 LocalPlanar(Vector3 velocity, float bodyYawDegrees)
        {
            Vector3 local = Quaternion.Euler(0, -bodyYawDegrees, 0) * new Vector3(velocity.x, 0, velocity.z);
            return new Vector2(local.x, local.z);
        }

        /// <summary>Pistols use the one-handed pose set; every other weapon (and unarmed) uses the rifle set.</summary>
        public static bool UsesPistolPoses(WeaponClass? weapon) => weapon == WeaponClass.Pistol;
    }

    /// <summary>
    /// Body yaw for a character that can turn in place. While moving or aiming the body faces the view. Standing still,
    /// the head and upper body follow the view and the feet stay planted until the view is more than
    /// <see cref="Threshold"/> degrees away; then the body turns to the view over <see cref="TurnSeconds"/>.
    /// </summary>
    public sealed class TurnInPlace
    {
        public const float Threshold = 60f;
        public const float TurnSeconds = 0.3f;
        /// <summary>How fast the body follows the view while moving (degrees per second).</summary>
        public const float FollowRate = 720f;

        private float _turnTarget, _turnRate;

        public float BodyYaw { get; private set; }
        public bool Turning { get; private set; }
        /// <summary>Signed angle of the last turn that started (degrees, positive = right).</summary>
        public float LastTurnAngle { get; private set; }

        public void Snap(float yaw)
        {
            BodyYaw = yaw;
            Turning = false;
        }

        /// <summary>Advances the body yaw; true on the frame a turn-in-place starts.</summary>
        public bool Update(float viewYaw, bool moving, bool aiming, float dt)
        {
            if (moving || aiming)
            {
                Turning = false;
                BodyYaw = Mathf.MoveTowardsAngle(BodyYaw, viewYaw, FollowRate * dt);
                return false;
            }
            bool started = false;
            float delta = Mathf.DeltaAngle(BodyYaw, viewYaw);
            if (!Turning && Mathf.Abs(delta) > Threshold)
            {
                Turning = true;
                started = true;
                LastTurnAngle = delta;
                _turnTarget = viewYaw;
                _turnRate = Mathf.Abs(delta) / TurnSeconds;
            }
            if (Turning)
            {
                _turnTarget = viewYaw; // keep tracking the view while the turn plays
                BodyYaw = Mathf.MoveTowardsAngle(BodyYaw, _turnTarget, Mathf.Max(_turnRate, 90f) * dt);
                if (Mathf.Abs(Mathf.DeltaAngle(BodyYaw, _turnTarget)) < 1f) Turning = false;
            }
            return started;
        }
    }
}
