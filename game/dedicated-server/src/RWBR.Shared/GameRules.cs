namespace RWBR.Shared
{
    /// <summary>Character and item constants shared by client prediction and server simulation (metres, seconds).</summary>
    public static class GameRules
    {
        // Movement
        public const float WalkSpeed = 5.0f;
        public const float SprintSpeed = 7.0f;
        public const float CrouchSpeed = 2.5f;
        /// <summary>Movement speed multiplier while aiming down sights (sprint is not possible while aiming).</summary>
        public const float AimMoveSpeedFactor = 0.7f;
        public const float JumpSpeed = 5.5f;
        public const float Gravity = 15f;
        public const float MaxFallSpeed = 30f;
        /// <summary>Highest ledge a character walks up without jumping (stairs, kerbs).</summary>
        public const float StepHeight = 0.45f;

        // Body
        public const float PlayerRadius = 0.4f;
        public const float PlayerHeight = 1.8f;
        public const float EyeHeight = 1.6f;
        public const float CrouchHeight = 1.2f;
        public const float CrouchEyeHeight = 1.0f;
        public const float MaxPitchRadians = 1.4f; // ~80 degrees

        // Health, armor, items
        public const float MaxHealth = 100f;
        public const float MaxArmor = 100f;
        /// <summary>Share of incoming body damage absorbed by a vest while it has points (see <see cref="Items"/>).</summary>
        public const float ArmorAbsorb = Items.VestAbsorb;
        public const float MedkitHeal = Items.MedkitHeal;
        public const float MedkitSeconds = Items.MedkitSeconds;
        /// <summary>Endless reserve on the firing range (matches limit ammo by backpack weight instead).</summary>
        public const int MaxReserveAmmo = 180;
        public const float PickupRange = 2.0f;
    }
}
