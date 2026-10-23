using UnityEngine;

namespace RWBR.Client.Player
{
    /// <summary>What the camera follows: a pivot point and view angles in degrees (pitch positive = down).</summary>
    public interface ICameraTarget
    {
        Vector3 CameraPivot { get; }
        float YawDegrees { get; }
        float PitchDegrees { get; }
        /// <summary>Aim blend, zoom and recoil kick; null for targets that never aim.</summary>
        AimState Aim { get; }
    }

    /// <summary>
    /// Over-the-shoulder camera. Pulls in at once when a wall is between it and the player and eases back out, moves
    /// closer and narrows the field of view while aiming, and goes to the eye for scoped weapons (3x and above).
    /// </summary>
    public sealed class ThirdPersonCamera : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour target;
        [SerializeField] private float distance = 3.6f;
        [SerializeField] private float shoulderOffset = 0.6f;
        [SerializeField] private float aimDistance = 1.7f;
        [SerializeField] private float aimShoulderOffset = 0.55f;
        [SerializeField] private float collisionRadius = 0.2f;
        [SerializeField] private float easeOutSeconds = 0.25f;

        private Camera _camera;
        private float _baseFov = 60f;
        private float _current = -1f;
        private float _velocity;

        public ICameraTarget Target
        {
            get => target as ICameraTarget;
            set => target = value as MonoBehaviour;
        }

        /// <summary>Distance from the pivot this frame (after aiming and collision).</summary>
        public float CurrentDistance => _current;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            if (_camera != null) _baseFov = _camera.fieldOfView;
        }

        private void LateUpdate()
        {
            ICameraTarget t = Target;
            if (t == null) return;
            AimState aim = t.Aim;
            float blend = aim?.Eased ?? 0f;
            bool scope = aim != null && aim.UsesScope;

            Quaternion rotation = Quaternion.Euler(t.PitchDegrees + (aim?.KickPitch ?? 0), t.YawDegrees + (aim?.KickYaw ?? 0), 0);
            float wanted = Mathf.Lerp(distance, scope ? 0f : aimDistance, blend);
            float shoulder = Mathf.Lerp(shoulderOffset, scope ? 0f : aimShoulderOffset, blend);
            Vector3 pivot = t.CameraPivot + rotation * Vector3.right * shoulder;
            Vector3 back = rotation * Vector3.back;

            float allowed = wanted;
            if (wanted > 0.01f && Physics.SphereCast(pivot, collisionRadius, back, out RaycastHit hit, wanted, ~0, QueryTriggerInteraction.Ignore))
                allowed = Mathf.Max(0.3f, hit.distance);

            // Snap in so walls never block the view; ease out so the camera does not jump when the wall clears.
            if (_current < 0 || allowed < _current || blend > 0.01f) _current = allowed;
            else _current = Mathf.SmoothDamp(_current, allowed, ref _velocity, easeOutSeconds);

            transform.SetPositionAndRotation(pivot + back * _current, rotation);
            if (_camera != null) _camera.fieldOfView = _baseFov / (aim?.FovDivisor ?? 1f);
        }
    }
}
