using RWBR.Client.Combat;
using RWBR.Shared;
using UnityEngine;

namespace RWBR.Client.Character
{
    /// <summary>
    /// Drives a humanoid Animator (the controller built by RWBR > Characters > Build) from <see cref="CharacterMotion"/>:
    /// locomotion blend parameters in the body's frame, crouch, jump and land, aim and pitch, reload, weapon switch, hit
    /// reactions and death. Turns the model in place when the view swings far enough. In the IK pass it puts both hands
    /// on the weapon's grips, aims the head and spine at the view, and plants the feet on the ground when standing.
    /// Gameplay never reads the animation: the shared simulation is authoritative and this is presentation only.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public sealed class CharacterAnimator : MonoBehaviour
    {
        private const float Damping = 0.1f;
        private const float FootRayUp = 0.5f;
        private const float FootRayDown = 0.9f;
        private const float FootOffset = 0.01f;

        [SerializeField] private float handRotationWeight;
        [SerializeField] private float lookWeight = 1f;

        private readonly TurnInPlace _turn = new TurnInPlace();
        private Animator _animator;
        private WeaponHolder _weapon;
        private CharacterMotion _motion;
        private bool _wasGrounded = true, _initialised;
        private WeaponClass? _weaponClass;
        private float _handWeight;
        private Vector3 _lookTarget;

        public Animator Animator => _animator;
        public TurnInPlace Turn => _turn;
        /// <summary>Feet planted by IK on the last IK pass (diagnostics and tests).</summary>
        public int FeetPlanted { get; private set; }
        /// <summary>Hand IK weight on the last IK pass.</summary>
        public float HandWeight => _handWeight;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _animator.applyRootMotion = false; // the simulation moves the character
            _animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
        }

        public void Bind(WeaponHolder weapon) => _weapon = weapon;

        /// <summary>Call once per rendered frame with the character's state.</summary>
        public void Drive(CharacterMotion m, float rootYawDegrees, float dt)
        {
            if (_animator == null || _animator.runtimeAnimatorController == null) return;
            if (!_initialised)
            {
                _turn.Snap(m.ViewYawDegrees);
                _wasGrounded = m.Grounded;
                _weaponClass = m.Weapon;
                _initialised = true;
            }
            _motion = m;

            Vector2 planar = new Vector2(m.Velocity.x, m.Velocity.z);
            bool moving = planar.magnitude > LocomotionMath.StillSpeed;
            if (_turn.Update(m.ViewYawDegrees, moving, m.Aim > 0.5f, dt) && m.Alive && m.Grounded)
            {
                _animator.SetFloat(AnimatorIds.TurnAngle, _turn.LastTurnAngle);
                _animator.SetTrigger(AnimatorIds.Turn);
            }
            // The root faces the view (the weapon is parented to it); the model turns to the body yaw underneath.
            transform.localRotation = Quaternion.Euler(0, Mathf.DeltaAngle(rootYawDegrees, _turn.BodyYaw), 0);

            Vector2 local = LocomotionMath.LocalPlanar(m.Velocity, _turn.BodyYaw);
            _animator.SetFloat(AnimatorIds.Forward, local.y, Damping, dt);
            _animator.SetFloat(AnimatorIds.Strafe, local.x, Damping, dt);
            _animator.SetFloat(AnimatorIds.Speed, planar.magnitude, Damping, dt);
            _animator.SetBool(AnimatorIds.Crouch, m.Crouched);
            _animator.SetBool(AnimatorIds.Grounded, m.Grounded);
            _animator.SetFloat(AnimatorIds.Aim, m.Aim);
            _animator.SetFloat(AnimatorIds.Pitch, m.PitchDegrees, Damping, dt);
            _animator.SetFloat(AnimatorIds.Pistol, LocomotionMath.UsesPistolPoses(m.Weapon) ? 1f : 0f, Damping, dt);
            _animator.SetBool(AnimatorIds.Armed, m.Weapon.HasValue);
            _animator.SetBool(AnimatorIds.Reloading, m.Reloading);
            _animator.SetBool(AnimatorIds.Dead, !m.Alive);

            if (m.Alive)
            {
                if (_wasGrounded && !m.Grounded && m.Velocity.y > 1f) _animator.SetTrigger(AnimatorIds.Jump);
                if (!_wasGrounded && m.Grounded) _animator.SetTrigger(AnimatorIds.Land);
                if (_weaponClass != m.Weapon && m.Weapon.HasValue) _animator.SetTrigger(AnimatorIds.Switch);
            }
            _wasGrounded = m.Grounded;
            _weaponClass = m.Weapon;

            // Hands come off the weapon while reloading (the reload clip moves them) and when unarmed or dead.
            float wantHands = m.Alive && m.Weapon.HasValue && !m.Reloading ? 1f : 0f;
            _handWeight = Mathf.MoveTowards(_handWeight, wantHands, dt / 0.15f);
            Quaternion view = Quaternion.Euler(m.PitchDegrees, m.ViewYawDegrees, 0);
            _lookTarget = transform.position + Vector3.up * 1.6f + view * Vector3.forward * 20f;
        }

        /// <summary>A shot fired: a short upper-body recoil on the reactions layer.</summary>
        public void Fired()
        {
            if (_animator != null && _animator.runtimeAnimatorController != null) _animator.SetTrigger(AnimatorIds.Fire);
        }

        /// <summary>Took a hit: a flinch on the reactions layer.</summary>
        public void Hit()
        {
            if (_animator != null && _animator.runtimeAnimatorController != null && _motion.Alive) _animator.SetTrigger(AnimatorIds.Hit);
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (layerIndex != 0 || !_motion.Alive) return;

            _animator.SetLookAtWeight(lookWeight, 0.35f, 0.8f, 0f, 0.6f);
            _animator.SetLookAtPosition(_lookTarget);

            WeaponModel w = _weapon?.Current;
            bool shown = w != null && w.Root.activeInHierarchy;
            float hands = shown ? _handWeight : 0f;
            SetHand(AvatarIKGoal.RightHand, shown ? w.RightHandGrip : null, hands);
            SetHand(AvatarIKGoal.LeftHand, shown ? w.LeftHandGrip : null, hands);

            // Feet: plant on the ground (stairs, kerbs) when standing; running clips keep their own foot placement.
            FeetPlanted = 0;
            bool standing = _motion.Grounded && new Vector2(_motion.Velocity.x, _motion.Velocity.z).magnitude < LocomotionMath.StillSpeed;
            PlantFoot(AvatarIKGoal.LeftFoot, standing);
            PlantFoot(AvatarIKGoal.RightFoot, standing);
        }

        private void SetHand(AvatarIKGoal goal, Transform target, float weight)
        {
            if (target == null || weight <= 0f)
            {
                _animator.SetIKPositionWeight(goal, 0f);
                _animator.SetIKRotationWeight(goal, 0f);
                return;
            }
            _animator.SetIKPositionWeight(goal, weight);
            _animator.SetIKRotationWeight(goal, weight * handRotationWeight);
            _animator.SetIKPosition(goal, target.position);
            _animator.SetIKRotation(goal, target.rotation);
        }

        private void PlantFoot(AvatarIKGoal goal, bool standing)
        {
            if (!standing)
            {
                _animator.SetIKPositionWeight(goal, 0f);
                _animator.SetIKRotationWeight(goal, 0f);
                return;
            }
            // The animated foot (ankle) bone; the IK goal sits the sole on the ground below it.
            bool left = goal == AvatarIKGoal.LeftFoot;
            Transform bone = _animator.GetBoneTransform(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
            if (bone == null)
            {
                _animator.SetIKPositionWeight(goal, 0f);
                return;
            }
            Vector3 foot = bone.position;
            if (!Physics.Raycast(foot + Vector3.up * FootRayUp, Vector3.down, out RaycastHit hit, FootRayUp + FootRayDown, ~0, QueryTriggerInteraction.Ignore))
            {
                _animator.SetIKPositionWeight(goal, 0f);
                return;
            }
            _animator.SetIKPositionWeight(goal, 1f);
            _animator.SetIKRotationWeight(goal, 1f);
            float sole = left ? _animator.leftFeetBottomHeight : _animator.rightFeetBottomHeight;
            _animator.SetIKPosition(goal, hit.point + Vector3.up * (sole + FootOffset));
            Quaternion along = Quaternion.FromToRotation(Vector3.up, hit.normal) * Quaternion.Euler(0, transform.eulerAngles.y, 0);
            _animator.SetIKRotation(goal, along);
            FeetPlanted++;
        }
    }
}
