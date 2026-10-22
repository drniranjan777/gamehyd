namespace RWBR.Shared
{
    /// <summary>Server-controlled, versioned weapon balance (BRD 5.3). Cosmetics never change these values.</summary>
    public sealed class WeaponDefinition
    {
        public string Id { get; set; } = "rifle_standard";
        public int BalanceVersion { get; set; } = 2;
        public float Damage { get; set; } = 20f;
        public float Range { get; set; } = 80f;
        public float FireIntervalSeconds { get; set; } = 0.12f;
        public int MagazineSize { get; set; } = 30;
        public float ReloadSeconds { get; set; } = 2.0f;

        /// <summary>Cone half-angle, standing still on the ground.</summary>
        public float SpreadDegrees { get; set; } = 0.8f;
        public float CrouchSpreadMultiplier { get; set; } = 0.6f;
        public float MovingSpreadDegrees { get; set; } = 1.5f;
        public float AirborneSpreadDegrees { get; set; } = 4f;

        /// <summary>View kick per shot. Applied by the client to the camera; the server only sees the resulting aim.</summary>
        public float RecoilPitchDegrees { get; set; } = 0.7f;
        public float RecoilYawDegrees { get; set; } = 0.25f;

        public static WeaponDefinition DefaultRifle() => new WeaponDefinition();

        public float SpreadFor(bool moving, bool crouched, bool airborne)
        {
            float spread = SpreadDegrees * (crouched ? CrouchSpreadMultiplier : 1f);
            if (moving) spread += MovingSpreadDegrees;
            if (airborne) spread += AirborneSpreadDegrees;
            return spread;
        }
    }
}
