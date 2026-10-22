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
    /// Health, armor, two weapon slots and item state for one character, advanced in ticks. Shared by the
    /// authoritative server and the offline client so both apply the same rules (BRD 5.2, 5.3). Weapon balance is
    /// passed in by the caller (catalog or a test override), never stored here.
    /// </summary>
    public sealed class Combatant
    {
        public const int SlotCount = 2;

        private readonly int[] _slotWeapon = { -1, -1 };
        private readonly int[] _slotAmmo = new int[SlotCount];
        // Per slot, so swapping weapons can never skip a weapon's own fire interval (no quick-swap exploit).
        private readonly long[] _slotNextFire = new long[SlotCount];

        public float Health { get; set; } = GameRules.MaxHealth;
        public float Armor { get; set; }
        public int ReserveAmmo { get; set; }
        public int Medkits { get; set; }
        public int ActiveSlot { get; private set; }

        public long NextFireTick
        {
            get => _slotNextFire[ActiveSlot];
            private set => _slotNextFire[ActiveSlot] = value;
        }
        public long ReloadDoneTick { get; private set; } = -1;
        public long HealDoneTick { get; private set; } = -1;

        public bool Alive => Health > 0;
        public bool Reloading => ReloadDoneTick >= 0;
        public bool Healing => HealDoneTick >= 0;

        /// <summary>Catalog index of the weapon in hand, or -1.</summary>
        public int ActiveWeaponIndex => _slotWeapon[ActiveSlot];
        public bool HasWeapon => ActiveWeaponIndex >= 0;
        public int SlotWeapon(int slot) => _slotWeapon[slot];
        public int SlotAmmo(int slot) => _slotAmmo[slot];

        /// <summary>Rounds in the active weapon's magazine.</summary>
        public int Ammo
        {
            get => _slotAmmo[ActiveSlot];
            set => _slotAmmo[ActiveSlot] = value;
        }

        public static int Ticks(float seconds, int tickRate) => (int)Math.Ceiling(seconds * tickRate);

        /// <summary>Respawn state: full health, empty slots, or <paramref name="weaponIndex"/> in slot 1 when armed.</summary>
        public void Reset(WeaponDefinition weapon, bool withWeapon, int reserveAmmo, int weaponIndex = WeaponCatalog.Rifle)
        {
            Health = GameRules.MaxHealth;
            Armor = 0;
            _slotWeapon[0] = withWeapon ? weaponIndex : -1;
            _slotWeapon[1] = -1;
            _slotAmmo[0] = withWeapon ? weapon.MagazineSize : 0;
            _slotAmmo[1] = 0;
            ActiveSlot = 0;
            ReserveAmmo = reserveAmmo;
            Medkits = 0;
            _slotNextFire[0] = _slotNextFire[1] = 0;
            ReloadDoneTick = -1;
            HealDoneTick = -1;
        }

        /// <summary>Completes reloads and heals whose time has come. Call once per tick before actions.</summary>
        public void Update(long tick, WeaponDefinition activeWeapon)
        {
            if (Reloading && tick >= ReloadDoneTick)
            {
                int take = Math.Min(activeWeapon.MagazineSize - Ammo, ReserveAmmo);
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

        /// <summary>Swaps to the other slot if it holds a weapon. Cancels reloads and heals; firing waits for the draw.</summary>
        public bool TrySwitch(long tick, WeaponDefinition next, int tickRate)
        {
            int other = 1 - ActiveSlot;
            if (_slotWeapon[other] < 0) return false;
            ActiveSlot = other;
            ReloadDoneTick = -1;
            HealDoneTick = -1;
            NextFireTick = Math.Max(NextFireTick, tick + Ticks(next.SwitchSeconds, tickRate)); // the drawn weapon's slot
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

        /// <summary>False when picking the item up would have no effect (weapon already carried, full, or better armor).</summary>
        public bool CanPickUp(LootSpawn item)
        {
            int weapon = item.WeaponIndex;
            if (weapon >= 0) return _slotWeapon[0] != weapon && _slotWeapon[1] != weapon;
            return item.Type switch
            {
                LootType.Ammo => ReserveAmmo < GameRules.MaxReserveAmmo,
                LootType.Medkit => Medkits < GameRules.MaxMedkits,
                LootType.Armor => Armor < Math.Min(GameRules.MaxArmor, item.Quantity),
                _ => false,
            };
        }

        /// <summary>
        /// Weapons go into an empty slot (and are drawn), otherwise replace the weapon in hand.
        /// <paramref name="weapon"/> is the balance for the picked-up weapon (magazine size).
        /// </summary>
        public void PickUp(LootSpawn item, WeaponDefinition weapon)
        {
            int index = item.WeaponIndex;
            if (index >= 0)
            {
                int slot = _slotWeapon[ActiveSlot] < 0 ? ActiveSlot : _slotWeapon[1 - ActiveSlot] < 0 ? 1 - ActiveSlot : ActiveSlot;
                _slotWeapon[slot] = index;
                _slotAmmo[slot] = weapon.MagazineSize;
                ActiveSlot = slot;
                ReloadDoneTick = -1;
                return;
            }
            switch (item.Type)
            {
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
