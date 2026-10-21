namespace RWBR.Shared
{
    /// <summary>Character constants shared by client prediction and server simulation (metres, seconds).</summary>
    public static class GameRules
    {
        public const float WalkSpeed = 5.0f;
        public const float SprintSpeed = 7.0f;
        public const float PlayerRadius = 0.4f;
        public const float PlayerHeight = 1.8f;
        public const float EyeHeight = 1.6f;
        public const float MaxHealth = 100f;
        public const float MaxPitchRadians = 1.4f; // ~80 degrees
    }
}
