using UnityEngine;

namespace RWBR.Client.Player
{
    /// <summary>What the camera follows: a pivot point and view angles in degrees (pitch positive = down).</summary>
    public interface ICameraTarget
    {
        Vector3 CameraPivot { get; }
        float YawDegrees { get; }
        float PitchDegrees { get; }
    }

    /// <summary>Over-the-shoulder camera that pulls in when a wall is between it and the player.</summary>
    public sealed class ThirdPersonCamera : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour target;
        [SerializeField] private float distance = 3.6f;
        [SerializeField] private float shoulderOffset = 0.6f;
        [SerializeField] private float collisionRadius = 0.2f;

        public ICameraTarget Target
        {
            get => target as ICameraTarget;
            set => target = value as MonoBehaviour;
        }

        private void LateUpdate()
        {
            ICameraTarget t = Target;
            if (t == null) return;
            Quaternion rotation = Quaternion.Euler(t.PitchDegrees, t.YawDegrees, 0);
            Vector3 pivot = t.CameraPivot + rotation * Vector3.right * shoulderOffset;
            Vector3 back = rotation * Vector3.back;

            float d = distance;
            if (Physics.SphereCast(pivot, collisionRadius, back, out RaycastHit hit, distance, ~0, QueryTriggerInteraction.Ignore))
                d = Mathf.Max(0.3f, hit.distance);

            transform.SetPositionAndRotation(pivot + back * d, rotation);
        }
    }
}
