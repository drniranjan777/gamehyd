using System.Numerics;

namespace RWBR.Shared
{
    /// <summary>Character movement state advanced by <see cref="Simulation.Step"/>. Position is at the feet.</summary>
    public struct MotorState
    {
        public Vector3 Position;
        public float VerticalVelocity;
        public bool Grounded;
        public bool Crouched;

        public float Height => Crouched ? GameRules.CrouchHeight : GameRules.PlayerHeight;
        public float EyeHeight => Crouched ? GameRules.CrouchEyeHeight : GameRules.EyeHeight;
        public Vector3 Eye => Position + new Vector3(0, EyeHeight, 0);

        public static MotorState StandingAt(Vector3 feet) => new MotorState { Position = feet, Grounded = true };
    }
}
