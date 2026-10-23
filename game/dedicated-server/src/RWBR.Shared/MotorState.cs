using System.Numerics;

namespace RWBR.Shared
{
    /// <summary>The plane drop at the start of a match: aboard the plane, falling, under a parachute, or landed (None).</summary>
    public enum DropPhase : byte
    {
        None = 0,
        Plane = 1,
        Freefall = 2,
        Parachute = 3,
    }

    /// <summary>Character movement state advanced by <see cref="Simulation.Step"/>. Position is at the feet.</summary>
    public struct MotorState
    {
        public Vector3 Position;
        public float VerticalVelocity;
        public bool Grounded;
        public bool Crouched;
        public DropPhase Drop;

        /// <summary>Aboard the plane, falling or under a parachute: no weapons, items or cars until landed.</summary>
        public bool Dropping => Drop != DropPhase.None;

        public float Height => Crouched ? GameRules.CrouchHeight : GameRules.PlayerHeight;
        public float EyeHeight => Crouched ? GameRules.CrouchEyeHeight : GameRules.EyeHeight;
        public Vector3 Eye => Position + new Vector3(0, EyeHeight, 0);

        public static MotorState StandingAt(Vector3 feet) => new MotorState { Position = feet, Grounded = true };
    }
}
