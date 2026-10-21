namespace RWBR.Shared
{
    /// <summary>Server-controlled, versioned weapon balance (BRD 5.3). Cosmetics never change these values.</summary>
    public sealed class WeaponDefinition
    {
        public string Id { get; set; } = "rifle_standard";
        public int BalanceVersion { get; set; } = 1;
        public float Damage { get; set; } = 20f;
        public float Range { get; set; } = 80f;
        public float FireIntervalSeconds { get; set; } = 0.12f;
        public int MagazineSize { get; set; } = 30;
        public float ReloadSeconds { get; set; } = 2.0f;

        public static WeaponDefinition DefaultRifle() => new WeaponDefinition();
    }
}
