#nullable enable
using System.Collections.Generic;

namespace RWBR.Shared
{
    public enum WeaponClass : byte
    {
        Pistol,
        Smg,
        AssaultRifle,
        Shotgun,
        Marksman,
        Sniper,
    }

    /// <summary>
    /// The server-controlled weapon roster (BRD 5.3). Indices are protocol values and must stay stable; balance
    /// changes bump <see cref="BalanceVersion"/> and are mirrored in the weapon_balance_versions table.
    /// All names and designs are original (no licensed real-world weapons).
    /// </summary>
    public static class WeaponCatalog
    {
        public const int BalanceVersion = 4;
        public const byte None = 255;

        public const byte Pistol = 0;
        public const byte Smg = 1;
        public const byte Rifle = 2;
        public const byte Shotgun = 3;
        public const byte Marksman = 4;
        public const byte Sniper = 5;

        private static readonly WeaponDefinition[] Weapons =
        {
            new WeaponDefinition
            {
                Id = "sidearm_p9", DisplayName = "P9 Sidearm", Class = WeaponClass.Pistol, BalanceVersion = BalanceVersion,
                Damage = 22, HeadshotMultiplier = 1.8f, Range = 50, FalloffStart = 15, MinDamageFactor = 0.6f,
                FireIntervalSeconds = 0.22f, MagazineSize = 12, ReloadSeconds = 1.4f,
                SpreadDegrees = 0.9f, MovingSpreadDegrees = 1.0f, AirborneSpreadDegrees = 3f,
                RecoilPitchDegrees = 1.1f, RecoilYawDegrees = 0.3f, SwitchSeconds = 0.3f,
                AimSpreadMultiplier = 0.6f, AimZoom = 1.25f,
            },
            new WeaponDefinition
            {
                Id = "kestrel_smg", DisplayName = "Kestrel SMG", Class = WeaponClass.Smg, BalanceVersion = BalanceVersion,
                Damage = 15, HeadshotMultiplier = 1.5f, Range = 60, FalloffStart = 12, MinDamageFactor = 0.5f,
                FireIntervalSeconds = 0.07f, MagazineSize = 32, ReloadSeconds = 1.8f,
                SpreadDegrees = 1.4f, MovingSpreadDegrees = 0.8f, AirborneSpreadDegrees = 3f,
                RecoilPitchDegrees = 0.45f, RecoilYawDegrees = 0.35f, SwitchSeconds = 0.4f,
                AimSpreadMultiplier = 0.65f, AimZoom = 1.25f,
            },
            new WeaponDefinition
            {
                Id = "rifle_standard", DisplayName = "Standard Rifle", Class = WeaponClass.AssaultRifle, BalanceVersion = BalanceVersion,
                Damage = 20, HeadshotMultiplier = 1.8f, Range = 120, FalloffStart = 40, MinDamageFactor = 0.7f,
                FireIntervalSeconds = 0.12f, MagazineSize = 30, ReloadSeconds = 2.0f,
                SpreadDegrees = 0.8f, MovingSpreadDegrees = 1.5f, AirborneSpreadDegrees = 4f,
                RecoilPitchDegrees = 0.7f, RecoilYawDegrees = 0.25f, SwitchSeconds = 0.5f,
                AimSpreadMultiplier = 0.5f, AimZoom = 1.5f,
            },
            new WeaponDefinition
            {
                Id = "pump_12", DisplayName = "Pump-12", Class = WeaponClass.Shotgun, BalanceVersion = BalanceVersion,
                Damage = 12, Pellets = 8, HeadshotMultiplier = 1.25f, Range = 30, FalloffStart = 6, MinDamageFactor = 0.2f,
                FireIntervalSeconds = 0.75f, MagazineSize = 6, ReloadSeconds = 2.6f,
                SpreadDegrees = 4.5f, CrouchSpreadMultiplier = 0.9f, MovingSpreadDegrees = 0.5f, AirborneSpreadDegrees = 1.5f,
                RecoilPitchDegrees = 3f, RecoilYawDegrees = 0.6f, SwitchSeconds = 0.5f,
                AimSpreadMultiplier = 0.85f, AimZoom = 1.15f,
            },
            new WeaponDefinition
            {
                Id = "marksman_dmr", DisplayName = "Marksman DMR", Class = WeaponClass.Marksman, BalanceVersion = BalanceVersion,
                Damage = 42, HeadshotMultiplier = 2f, Range = 250, FalloffStart = 120, MinDamageFactor = 0.8f,
                FireIntervalSeconds = 0.35f, MagazineSize = 10, ReloadSeconds = 2.4f,
                SpreadDegrees = 0.25f, MovingSpreadDegrees = 2.5f, AirborneSpreadDegrees = 6f,
                RecoilPitchDegrees = 1.8f, RecoilYawDegrees = 0.3f, SwitchSeconds = 0.6f,
                AimSpreadMultiplier = 0.35f, AimZoom = 3f,
            },
            new WeaponDefinition
            {
                Id = "longshot_sr", DisplayName = "Longshot", Class = WeaponClass.Sniper, BalanceVersion = BalanceVersion,
                Damage = 85, HeadshotMultiplier = 2.5f, Range = 400, FalloffStart = 300, MinDamageFactor = 0.9f,
                FireIntervalSeconds = 1.4f, MagazineSize = 5, ReloadSeconds = 3.0f,
                SpreadDegrees = 0.05f, MovingSpreadDegrees = 4f, AirborneSpreadDegrees = 8f,
                RecoilPitchDegrees = 4f, RecoilYawDegrees = 0.5f, SwitchSeconds = 0.8f,
                AimSpreadMultiplier = 0.2f, AimZoom = 6f,
            },
        };

        public static IReadOnlyList<WeaponDefinition> All => Weapons;
        public static int Count => Weapons.Length;

        public static bool IsValid(int index) => index >= 0 && index < Weapons.Length;

        public static WeaponDefinition Get(int index) => Weapons[index];

        public static int IndexOf(string id)
        {
            for (int i = 0; i < Weapons.Length; i++)
                if (Weapons[i].Id == id) return i;
            return -1;
        }
    }
}
