using System;

namespace RWBR.Shared
{
    public enum FireResult
    {
        Fired,
        NoWeapon,
        Cooldown,
        Reloading,
        Healing,
        /// <summary>Magazine empty: a reload was started if reserve ammo allows.</summary>
        Empty,
    }

    /// <summary>
    /// Health, armor, weapon and item state for one character, advanced in ticks. Shared by the authoritative
    /// server and the offline client so both apply the same rules (BRD 5.2, 5.3).
    /// </summary>
    public sealed class Combatant
    {
        public float Health { get; set; } = GameRules.MaxHealth;
        public float Armor { get; set; }
        public bool HasWeapon { get; set; }
        public int Ammo { get; set; }
        public int ReserveAmmo { get; set; }
        public int Medkits { get; set; }

        public long NextFireTick { get; private set; }
        public long ReloadDoneTick { get; private set; } = -1;
        public long HealDoneTick { get; private set; } = -1;

        public bool Alive => Health > 0;
        public bool Reloading => ReloadDoneTick >= 0;
        public bool Healing => HealDoneTick >= 0;

        public static int Ticks(float seconds, int tickRate) => (int)Math.Ceiling(seconds * tickRate);

        public void Reset(WeaponDefinition weapon, bool withWeapon, int reserveAmmo)
        {
            Health = GameRules.MaxHealth;
            Armor = 0;
            HasWeapon = withWeapon;
            Ammo = withWeapon ? weapon.MagazineSize : 0;
            ReserveAmmo = reserveAmmo;
            Medkits = 0;
            NextFireTick = 0;
            ReloadDoneTick = -1;
            HealDoneTick = -1;
        }

        /// <summary>Completes reloads and heals whose time has come. Call once per tick before actions.</summary>
        public void Update(long tick, WeaponDefinition weapon)
        {
            if (Reloading && tick >= ReloadDoneTick)
            {
                int take = Math.Min(weapon.MagazineSize - Ammo, ReserveAmmo);
                Ammo += take;
                ReserveAmmo -= take;
                ReloadDoneTick = -1;
            }
            if (Healing && tick >= HealDoneTick)
            {
                Health = Math.Min(GameRules.MaxHealth, Health + GameRules.MedkitHeal);
                Medkits--;
                HealDoneTick = -1;
            }
        }

        public FireResult TryFire(long tick, WeaponDefinition weapon, int tickRate)
        {
            if (!HasWeapon) return FireResult.NoWeapon;
            if (Healing) return FireResult.Healing;
            if (Reloading) return FireResult.Reloading;
            if (tick < NextFireTick) return FireResult.Cooldown;
            if (Ammo <= 0)
            {
                TryStartReload(tick, weapon, tickRate);
                return FireResult.Empty;
            }
            Ammo--;
            NextFireTick = tick + Math.Max(1, Ticks(weapon.FireIntervalSeconds, tickRate));
            if (Ammo == 0) TryStartReload(tick, weapon, tickRate);
            return FireResult.Fired;
        }

        public bool TryStartReload(long tick, WeaponDefinition weapon, int tickRate)
        {
            if (!HasWeapon || Reloading || Ammo >= weapon.MagazineSize || ReserveAmmo <= 0) return false;
            HealDoneTick = -1;
            ReloadDoneTick = tick + Ticks(weapon.ReloadSeconds, tickRate);
            return true;
        }

        public bool TryStartHeal(long tick, int tickRate)
        {
            if (Medkits <= 0 || Healing || Health >= GameRules.MaxHealth) return false;
            ReloadDoneTick = -1;
            HealDoneTick = tick + Ticks(GameRules.MedkitSeconds, tickRate);
            return true;
        }

        public void CancelHeal() => HealDoneTick = -1;

        /// <summary>Applies damage through armor. Returns the health actually lost.</summary>
        public float ApplyDamage(float damage)
        {
            if (damage <= 0 || !Alive) return 0;
            float absorbed = Math.Min(Armor, damage * GameRules.ArmorAbsorb);
            Armor -= absorbed;
            float lost = Math.Min(Health, damage - absorbed);
            Health -= damage - absorbed;
            if (Health < 0) Health = 0;
            return lost;
        }

        /// <summary>False when picking the item up would have no effect (already owned, full, or better armor).</summary>
        public bool CanPickUp(LootSpawn item) => item.Type switch
        {
            LootType.Rifle => !HasWeapon,
            LootType.Ammo => ReserveAmmo < GameRules.MaxReserveAmmo,
            LootType.Medkit => Medkits < GameRules.MaxMedkits,
            LootType.Armor => Armor < Math.Min(GameRules.MaxArmor, item.Quantity),
            _ => false,
        };

        public void PickUp(LootSpawn item, WeaponDefinition weapon)
        {
            switch (item.Type)
            {
                case LootType.Rifle:
                    HasWeapon = true;
                    Ammo = weapon.MagazineSize;
                    break;
                case LootType.Ammo:
                    ReserveAmmo = Math.Min(GameRules.MaxReserveAmmo, ReserveAmmo + item.Quantity);
                    break;
                case LootType.Medkit:
                    Medkits = Math.Min(GameRules.MaxMedkits, Medkits + item.Quantity);
                    break;
                case LootType.Armor:
                    Armor = Math.Max(Armor, Math.Min(GameRules.MaxArmor, item.Quantity));
                    break;
            }
        }
    }
}
