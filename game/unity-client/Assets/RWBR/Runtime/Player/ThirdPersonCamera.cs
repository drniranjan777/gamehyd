using UnityEngine;

namespace RWBR.Client.Player
{
    /// <summary>Over-the-shoulder camera that pulls in when a wall is between it and the player.</summary>
    public sealed class ThirdPersonCamera : MonoBehaviour
    {
        [SerializeField] private GreyboxPlayer target;
        [SerializeField] private float distance = 4f;
        [SerializeField] private float pivotHeight = 1.7f;
        [SerializeField] private float shoulderOffset = 0.6f;
        [SerializeField] private float collisionRadius = 0.2f;

        public GreyboxPlayer Target
        {
            get => target;
            set => target = value;
        }

        private void LateUpdate()
        {
            if (target == null) return;
            Quaternion rotation = Quaternion.Euler(target.PitchDegrees, target.YawDegrees, 0);
            Vector3 pivot = target.transform.position + Vector3.up * pivotHeight + rotation * Vector3.right * shoulderOffset;
            Vector3 back = rotation * Vector3.back;

            float d = distance;
            if (Physics.SphereCast(pivot, collisionRadius, back, out RaycastHit hit, distance, ~0, QueryTriggerInteraction.Ignore))
                d = Mathf.Max(0.3f, hit.distance);

            transform.SetPositionAndRotation(pivot + back * d, rotation);
        }
    }
}
