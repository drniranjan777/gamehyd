#nullable enable
using System;
using System.Collections.Generic;

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
    /// Health, equipment, two weapon slots and the backpack for one character, advanced in ticks. Shared by the
    /// authoritative server, offline matches and practice so all apply the same rules (BRD 5.2, 5.3).
    /// <para>
    /// The backpack holds ammo (one count per calibre), heals, throwables and spare attachments, up to a weight set by
    /// the backpack level. Vests and helmets have points that absorb part of body and head damage. Attachments fitted
    /// to a weapon change its balance (<see cref="Items.WithAttachments"/>). Anything that has to go on the ground
    /// (a replaced weapon or vest, a dropped item) is added to <see cref="Dropped"/> for the owner to place.
    /// </para>
    /// </summary>
    public sealed class Combatant
    {
        public const int SlotCount = 2;
        /// <summary>Time to throw a grenade; nothing else can be done meanwhile.</summary>
        public const float ThrowSeconds = 0.8f;

        private readonly int[] _slotWeapon = { -1, -1 };
        private readonly int[] _slotAmmo = new int[SlotCount];
        private readonly Attachments[] _slotAttach = new Attachments[SlotCount];
        // Per slot, so swapping weapons can never skip a weapon's own fire interval (no quick-swap exploit).
        private readonly long[] _slotNextFire = new long[SlotCount];
        private readonly int[] _items = new int[Items.KindCount];
        private float _vestPoints, _helmetPoints;
        private float _boostPerTick;

        /// <summary>Base balance for a catalog index (the match config may override the catalog).</summary>
        public Func<int, WeaponDefinition> Balance { get; set; } = WeaponCatalog.Get;

        public float Health { get; set; } = GameRules.MaxHealth;
        public int ActiveSlot { get; private set; }
        public int VestLevel { get; private set; }
        public int HelmetLevel { get; private set; }
        public int BackpackLevel { get; private set; }

        /// <summary>Vest points (absorb body damage). Setting points without a vest equips the matching level.</summary>
        public float Armor
        {
            get => _vestPoints;
            set
            {
                _vestPoints = Math.Max(0, value);
                if (_vestPoints <= 0) VestLevel = 0;
                else if (VestLevel == 0) VestLevel = _vestPoints > Items.ArmorPoints[2] ? 3 : _vestPoints > Items.ArmorPoints[1] ? 2 : 1;
            }
        }

        public float HelmetPoints => _helmetPoints;

        /// <summary>Rounds in the backpack for the weapon in hand (0 when unarmed). Setting it ignores the weight limit.</summary>
        public int ReserveAmmo
        {
            get => HasWeapon ? _items[(int)ActiveAmmoKind] : 0;
            set
            {
                if (HasWeapon) _items[(int)ActiveAmmoKind] = Math.Max(0, value);
            }
        }

        public int Medkits
        {
            get => _items[(int)ItemKind.Medkit];
            set => _items[(int)ItemKind.Medkit] = Math.Max(0, value);
        }

        public long NextFireTick
        {
            get => _slotNextFire[ActiveSlot];
            private set => _slotNextFire[ActiveSlot] = value;
        }
        public long ReloadDoneTick { get; private set; } = -1;
        public long HealDoneTick { get; private set; } = -1;
        /// <summary>The heal being used while <see cref="Healing"/>.</summary>
        public ItemKind HealKind { get; private set; }
        public long BoostUntilTick { get; private set; } = -1;
        public long ThrowDoneTick { get; private set; } = -1;

        public bool Alive => Health > 0;
        public bool Reloading => ReloadDoneTick >= 0;
        public bool Healing => HealDoneTick >= 0;
        public bool Throwing => ThrowDoneTick >= 0;

        /// <summary>Catalog index of the weapon in hand, or -1.</summary>
        public int ActiveWeaponIndex => _slotWeapon[ActiveSlot];
        public bool HasWeapon => ActiveWeaponIndex >= 0;
        public int SlotWeapon(int slot) => _slotWeapon[slot];
        public int SlotAmmo(int slot) => _slotAmmo[slot];
        public Attachments SlotAttachments(int slot) => _slotAttach[slot];

        /// <summary>Balance of the weapon in a slot with its attachments, or null when the slot is empty.</summary>
        public WeaponDefinition? WeaponIn(int slot) =>
            _slotWeapon[slot] < 0 ? null : Items.WithAttachments(Balance(_slotWeapon[slot]), _slotAttach[slot]);

        public WeaponDefinition? ActiveWeapon => WeaponIn(ActiveSlot);
        public ItemKind ActiveAmmoKind => HasWeapon ? Items.AmmoFor(Balance(ActiveWeaponIndex).Class) : ItemKind.None;

        /// <summary>Rounds in the active weapon's magazine.</summary>
        public int Ammo
        {
            get => _slotAmmo[ActiveSlot];
            set => _slotAmmo[ActiveSlot] = value;
        }

        /// <summary>Units of a backpack item.</summary>
        public int Count(ItemKind kind) => _items[(int)kind];

        /// <summary>Sets a backpack item's count, ignoring the weight limit (practice, tests).</summary>
        public void SetCount(ItemKind kind, int count) => _items[(int)kind] = Math.Max(0, count);

        public int Capacity => Items.Capacity[BackpackLevel];

        public float Weight
        {
            get
            {
                float w = 0;
                for (int k = 0; k < _items.Length; k++)
                    if (_items[k] > 0) w += _items[k] * Items.Weight((ItemKind)k);
                return w;
            }
        }

        /// <summary>Items that have to be placed on the ground (replaced equipment, drops). The owner empties it.</summary>
        public List<ItemStack> Dropped { get; } = new List<ItemStack>();

        public static int Ticks(float seconds, int tickRate) => (int)Math.Ceiling(seconds * tickRate);

        /// <summary>Respawn state: full health, nothing carried, or <paramref name="weaponIndex"/> in slot 1 when armed
        /// with <paramref name="reserveAmmo"/> rounds of its calibre.</summary>
        public void Reset(WeaponDefinition weapon, bool withWeapon, int reserveAmmo, int weaponIndex = WeaponCatalog.Rifle)
        {
            Health = GameRules.MaxHealth;
            _vestPoints = _helmetPoints = 0;
            VestLevel = HelmetLevel = BackpackLevel = 0;
            Array.Clear(_items, 0, _items.Length);
            _slotWeapon[0] = withWeapon ? weaponIndex : -1;
            _slotWeapon[1] = -1;
            _slotAmmo[0] = withWeapon ? weapon.MagazineSize : 0;
            _slotAmmo[1] = 0;
            _slotAttach[0] = _slotAttach[1] = Attachments.None;
            ActiveSlot = 0;
            if (withWeapon) _items[(int)Items.AmmoFor(weapon.Class)] = Math.Max(0, reserveAmmo);
            _slotNextFire[0] = _slotNextFire[1] = 0;
            ReloadDoneTick = HealDoneTick = BoostUntilTick = ThrowDoneTick = -1;
            HealKind = ItemKind.None;
            Dropped.Clear();
        }

        /// <summary>Completes reloads, heals and throws whose time has come and applies boost healing. Call once per tick.</summary>
        public void Update(long tick, WeaponDefinition activeWeapon)
        {
            if (Reloading && tick >= ReloadDoneTick)
            {
                int k = (int)Items.AmmoFor(activeWeapon.Class);
                int take = Math.Min(activeWeapon.MagazineSize - Ammo, _items[k]);
                if (take > 0)
                {
                    Ammo += take;
                    _items[k] -= take;
                }
                ReloadDoneTick = -1;
            }
            // Boost healing runs on the ticks after the drink, up to and including BoostUntilTick.
            if (BoostUntilTick >= 0)
            {
                if (tick > BoostUntilTick || !Alive) BoostUntilTick = -1;
                else Health = Math.Min(GameRules.MaxHealth, Health + _boostPerTick);
            }
            if (Healing && tick >= HealDoneTick)
            {
                FinishHeal(tick);
                HealDoneTick = -1;
            }
            if (Throwing && tick >= ThrowDoneTick) ThrowDoneTick = -1;
        }

        private void FinishHeal(long tick)
        {
            int k = (int)HealKind;
            if (_items[k] <= 0) return;
            _items[k]--;
            switch (HealKind)
            {
                case ItemKind.Bandage:
                    Health = Math.Max(Health, Math.Min(Items.BandageCap, Health + Items.BandageHeal));
                    break;
                case ItemKind.Medkit:
                    Health = Math.Min(GameRules.MaxHealth, Health + Items.MedkitHeal);
                    break;
                case ItemKind.EnergyDrink:
                    BoostUntilTick = tick + _boostTicks;
                    break;
            }
        }

        private int _boostTicks = 1;

        public FireResult TryFire(long tick, WeaponDefinition weapon, int tickRate)
        {
            if (!HasWeapon) return FireResult.NoWeapon;
            if (Healing || Throwing) return FireResult.Healing;
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
            if (!HasWeapon || Reloading || Throwing || Ammo >= weapon.MagazineSize || _items[(int)Items.AmmoFor(weapon.Class)] <= 0) return false;
            HealDoneTick = -1;
            ReloadDoneTick = tick + Ticks(weapon.ReloadSeconds, tickRate);
            return true;
        }

        /// <summary>Swaps to the other slot if it holds a weapon. Cancels reloads and heals; firing waits for the draw.</summary>
        public bool TrySwitch(long tick, WeaponDefinition next, int tickRate)
        {
            int other = 1 - ActiveSlot;
            if (_slotWeapon[other] < 0 || Throwing) return false;
            ActiveSlot = other;
            ReloadDoneTick = -1;
            HealDoneTick = -1;
            NextFireTick = Math.Max(NextFireTick, tick + Ticks(next.SwitchSeconds, tickRate)); // the drawn weapon's slot
            return true;
        }

        /// <summary>
        /// The heal to use for "heal" without a choice: a medkit when badly hurt, bandages up to their cap, then an energy
        /// drink, then a medkit. None when nothing useful is carried.
        /// </summary>
        public ItemKind BestHeal()
        {
            if (Health >= GameRules.MaxHealth) return ItemKind.None;
            if (Health < 40 && Count(ItemKind.Medkit) > 0) return ItemKind.Medkit;
            if (Health < Items.BandageCap && Count(ItemKind.Bandage) > 0) return ItemKind.Bandage;
            if (Count(ItemKind.EnergyDrink) > 0 && BoostUntilTick < 0) return ItemKind.EnergyDrink;
            if (Count(ItemKind.Medkit) > 0) return ItemKind.Medkit;
            return ItemKind.None;
        }

        public static float HealSeconds(ItemKind kind) =>
            kind == ItemKind.Bandage ? Items.BandageSeconds : kind == ItemKind.EnergyDrink ? Items.DrinkSeconds : Items.MedkitSeconds;

        /// <summary>Starts using a heal (<see cref="BestHeal"/> when <paramref name="kind"/> is None).</summary>
        public bool TryStartHeal(long tick, int tickRate, ItemKind kind = ItemKind.None)
        {
            if (kind == ItemKind.None) kind = BestHeal();
            if (Items.Category(kind) != ItemCategory.Heal || _items[(int)kind] <= 0 || Healing || Throwing) return false;
            if (Health >= GameRules.MaxHealth) return false;
            if (kind == ItemKind.Bandage && Health >= Items.BandageCap) return false;
            ReloadDoneTick = -1;
            HealKind = kind;
            HealDoneTick = tick + Ticks(HealSeconds(kind), tickRate);
            _boostTicks = Math.Max(1, Ticks(Items.BoostSeconds, tickRate));
            _boostPerTick = Items.DrinkHeal / _boostTicks;
            return true;
        }

        public void CancelHeal() => HealDoneTick = -1;

        /// <summary>Uses up one grenade of <paramref name="kind"/> if carried; the caller launches it.</summary>
        public bool TryThrow(long tick, ItemKind kind, int tickRate)
        {
            if (Items.Category(kind) != ItemCategory.Throwable || _items[(int)kind] <= 0 || Throwing) return false;
            _items[(int)kind]--;
            ReloadDoneTick = -1;
            HealDoneTick = -1;
            ThrowDoneTick = tick + Ticks(ThrowSeconds, tickRate);
            return true;
        }

        /// <summary>Applies damage through the helmet (head) or vest (body). Returns the health actually lost.</summary>
        public float ApplyDamage(float damage, bool headshot = false)
        {
            if (damage <= 0 || !Alive) return 0;
            float absorbed;
            if (headshot)
            {
                absorbed = Math.Min(_helmetPoints, damage * Items.HelmetAbsorb);
                _helmetPoints -= absorbed;
                if (_helmetPoints <= 0 && HelmetLevel > 0)
                {
                    _helmetPoints = 0;
                    HelmetLevel = 0; // broken
                }
            }
            else
            {
                absorbed = Math.Min(_vestPoints, damage * Items.VestAbsorb);
                _vestPoints -= absorbed;
                if (_vestPoints <= 0) VestLevel = 0;
            }
            float lost = Math.Min(Health, damage - absorbed);
            Health -= damage - absorbed;
            if (Health < 0) Health = 0;
            return lost;
        }

        /// <summary>False when picking the item up would have no effect (weapon already carried, no room, weaker armour).</summary>
        public bool CanPickUp(LootSpawn item) => CanTake(item.Kind, item.Count) > 0;

        /// <summary>
        /// How much of an item could be taken now: units for backpack items (limited by weight), 1 for anything else that
        /// would be an upgrade or fill a slot, 0 when it would have no effect.
        /// </summary>
        public int CanTake(ItemKind kind, int count)
        {
            switch (Items.Category(kind))
            {
                case ItemCategory.Weapon:
                    return WeaponCatalog.IsValid(count) && _slotWeapon[0] != count && _slotWeapon[1] != count ? 1 : 0;
                case ItemCategory.Vest:
                    return Items.Level(kind) > VestLevel || (Items.Level(kind) == VestLevel && count > _vestPoints) ? 1 : 0;
                case ItemCategory.Helmet:
                    return Items.Level(kind) > HelmetLevel || (Items.Level(kind) == HelmetLevel && count > _helmetPoints) ? 1 : 0;
                case ItemCategory.Backpack:
                    return Items.Level(kind) > BackpackLevel ? 1 : 0;
                case ItemCategory.Attachment:
                    if (FitSlot(Items.AttachmentBit(kind)) >= 0) return 1;
                    return RoomFor(kind, 1);
                case ItemCategory.Ammo:
                case ItemCategory.Heal:
                case ItemCategory.Throwable:
                    return RoomFor(kind, count);
                default:
                    return 0;
            }
        }

        private int RoomFor(ItemKind kind, int count)
        {
            float unit = Items.Weight(kind);
            if (unit <= 0) return count;
            int fit = (int)Math.Floor((Capacity - Weight) / unit + 1e-4);
            return Math.Max(0, Math.Min(count, fit));
        }

        /// <summary>Slot whose weapon takes <paramref name="a"/> and doesn't have it yet (active slot first), else -1.</summary>
        private int FitSlot(Attachments a)
        {
            foreach (int slot in new[] { ActiveSlot, 1 - ActiveSlot })
            {
                int w = _slotWeapon[slot];
                if (w >= 0 && (_slotAttach[slot] & a) == 0 && Items.Fits(a, Balance(w).Class)) return slot;
            }
            return -1;
        }

        /// <summary>Picks up a map loot spot. <paramref name="weapon"/> is unused (kept for callers); balance comes from <see cref="Balance"/>.</summary>
        public void PickUp(LootSpawn item, WeaponDefinition weapon) => Take(new ItemStack(item.Kind, item.Count, -1));

        /// <summary>
        /// Takes as much of <paramref name="stack"/> as fits and returns the units taken (0 = nothing). A weapon goes into
        /// an empty slot (and is drawn), otherwise replaces the weapon in hand, which is dropped. Better armour replaces
        /// worse, which is dropped. Attachments fit a weapon that takes them, or go in the backpack. A weapon fresh from
        /// the map (<see cref="ItemStack.Ammo"/> negative) has a full magazine and a spare one in the backpack.
        /// </summary>
        public int Take(ItemStack stack)
        {
            int can = CanTake(stack.Kind, stack.Count);
            if (can <= 0) return 0;
            switch (Items.Category(stack.Kind))
            {
                case ItemCategory.Weapon:
                    TakeWeapon(stack);
                    return 1;
                case ItemCategory.Vest:
                    if (VestLevel > 0) Dropped.Add(new ItemStack(Items.Vest(VestLevel), (int)Math.Ceiling(_vestPoints)));
                    VestLevel = Items.Level(stack.Kind);
                    _vestPoints = stack.Count;
                    return 1;
                case ItemCategory.Helmet:
                    if (HelmetLevel > 0) Dropped.Add(new ItemStack(Items.Helmet(HelmetLevel), (int)Math.Ceiling(_helmetPoints)));
                    HelmetLevel = Items.Level(stack.Kind);
                    _helmetPoints = stack.Count;
                    return 1;
                case ItemCategory.Backpack:
                    if (BackpackLevel > 0) Dropped.Add(new ItemStack(Items.Backpack(BackpackLevel), 1));
                    BackpackLevel = Items.Level(stack.Kind);
                    return 1;
                case ItemCategory.Attachment:
                    int slot = FitSlot(Items.AttachmentBit(stack.Kind));
                    if (slot >= 0) _slotAttach[slot] |= Items.AttachmentBit(stack.Kind);
                    else _items[(int)stack.Kind]++;
                    return 1;
                default:
                    _items[(int)stack.Kind] += can;
                    return can;
            }
        }

        private void TakeWeapon(ItemStack stack)
        {
            int slot = _slotWeapon[ActiveSlot] < 0 ? ActiveSlot : _slotWeapon[1 - ActiveSlot] < 0 ? 1 - ActiveSlot : ActiveSlot;
            if (_slotWeapon[slot] >= 0) Dropped.Add(new ItemStack(ItemKind.Weapon, _slotWeapon[slot], _slotAmmo[slot], _slotAttach[slot]));
            _slotWeapon[slot] = stack.Count;
            _slotAttach[slot] = stack.Attach;
            // Spare attachments in the backpack go straight onto the new weapon when it takes them.
            WeaponClass c = Balance(stack.Count).Class;
            foreach (ItemKind a in Items.AttachmentKinds)
            {
                Attachments bit = Items.AttachmentBit(a);
                if (_items[(int)a] > 0 && (_slotAttach[slot] & bit) == 0 && Items.Fits(bit, c))
                {
                    _items[(int)a]--;
                    _slotAttach[slot] |= bit;
                }
            }
            WeaponDefinition fitted = WeaponIn(slot)!;
            _slotAmmo[slot] = stack.Ammo < 0 ? fitted.MagazineSize : Math.Min(stack.Ammo, fitted.MagazineSize);
            // A weapon from a map loot spot comes with a spare magazine of its ammo, as far as the backpack allows.
            if (stack.Ammo < 0)
            {
                ItemKind ammo = Items.AmmoFor(c);
                _items[(int)ammo] += RoomFor(ammo, Balance(stack.Count).MagazineSize);
            }
            ActiveSlot = slot;
            ReloadDoneTick = -1;
        }

        /// <summary>
        /// Drops up to <paramref name="count"/> units of a backpack item (0 = all), the vest, helmet or backpack, a spare
        /// attachment (else the one on the weapon in hand), or the weapon in slot <paramref name="count"/>. The dropped
        /// pile goes to <see cref="Dropped"/>. A backpack can't be dropped while the rest wouldn't fit without it.
        /// </summary>
        public bool Drop(ItemKind kind, int count)
        {
            switch (Items.Category(kind))
            {
                case ItemCategory.Weapon:
                    int slot = count == 1 ? 1 : 0;
                    if (_slotWeapon[slot] < 0) return false;
                    Dropped.Add(new ItemStack(ItemKind.Weapon, _slotWeapon[slot], _slotAmmo[slot], _slotAttach[slot]));
                    _slotWeapon[slot] = -1;
                    _slotAmmo[slot] = 0;
                    _slotAttach[slot] = Attachments.None;
                    if (slot == ActiveSlot)
                    {
                        ReloadDoneTick = -1;
                        if (_slotWeapon[1 - slot] >= 0) ActiveSlot = 1 - slot;
                    }
                    return true;
                case ItemCategory.Vest:
                    if (VestLevel == 0) return false;
                    Dropped.Add(new ItemStack(Items.Vest(VestLevel), (int)Math.Ceiling(_vestPoints)));
                    VestLevel = 0;
                    _vestPoints = 0;
                    return true;
                case ItemCategory.Helmet:
                    if (HelmetLevel == 0) return false;
                    Dropped.Add(new ItemStack(Items.Helmet(HelmetLevel), (int)Math.Ceiling(_helmetPoints)));
                    HelmetLevel = 0;
                    _helmetPoints = 0;
                    return true;
                case ItemCategory.Backpack:
                    if (BackpackLevel == 0 || Weight > Items.Capacity[0]) return false;
                    Dropped.Add(new ItemStack(Items.Backpack(BackpackLevel), 1));
                    BackpackLevel = 0;
                    return true;
                case ItemCategory.Attachment:
                    if (_items[(int)kind] > 0)
                    {
                        _items[(int)kind]--;
                        Dropped.Add(new ItemStack(kind, 1));
                        return true;
                    }
                    Attachments bit = Items.AttachmentBit(kind);
                    if (!HasWeapon || (_slotAttach[ActiveSlot] & bit) == 0) return false;
                    _slotAttach[ActiveSlot] &= ~bit;
                    int mag = WeaponIn(ActiveSlot)!.MagazineSize;
                    if (Ammo > mag)
                    {
                        _items[(int)ActiveAmmoKind] += Ammo - mag; // rounds that no longer fit go back to the backpack
                        Ammo = mag;
                    }
                    Dropped.Add(new ItemStack(kind, 1));
                    return true;
                case ItemCategory.Ammo:
                case ItemCategory.Heal:
                case ItemCategory.Throwable:
                    int have = _items[(int)kind];
                    if (have <= 0) return false;
                    int n = count <= 0 ? have : Math.Min(count, have);
                    if (Healing && kind == HealKind && n == have) HealDoneTick = -1;
                    _items[(int)kind] -= n;
                    Dropped.Add(new ItemStack(kind, n));
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The recipient's inventory as a snapshot describes it, so a client can ask the same questions the server will
        /// (what can be picked up, what fits) before sending an input.
        /// </summary>
        public static Combatant FromSnapshot(Protocol.SnapshotPacket s, Protocol.SnapshotEntry self)
        {
            var c = new Combatant { Health = self.Health };
            for (int slot = 0; slot < SlotCount; slot++)
            {
                c._slotWeapon[slot] = s.SelfSlotWeapon[slot] == WeaponCatalog.None ? -1 : s.SelfSlotWeapon[slot];
                c._slotAmmo[slot] = s.SelfSlotAmmo[slot];
                c._slotAttach[slot] = (Attachments)s.SelfSlotAttach[slot];
            }
            c.ActiveSlot = Math.Min(s.SelfActiveSlot, (byte)(SlotCount - 1));
            c.VestLevel = s.SelfVest;
            c._vestPoints = self.Armor;
            c.HelmetLevel = s.SelfHelmet;
            c._helmetPoints = s.SelfHelmetPoints;
            c.BackpackLevel = Math.Min((int)s.SelfBackpack, 3);
            foreach (Protocol.ItemCount item in s.SelfItems)
                if ((int)item.Kind < Items.KindCount) c._items[(int)item.Kind] = item.Count;
            return c;
        }

        /// <summary>Everything carried, as piles for the ground (an eliminated player's loot). Leaves the character empty.</summary>
        public void DropAll()
        {
            for (int slot = 0; slot < SlotCount; slot++)
                if (_slotWeapon[slot] >= 0) Drop(ItemKind.Weapon, slot);
            for (int k = 0; k < _items.Length; k++)
                if (_items[k] > 0) Drop((ItemKind)k, 0);
            foreach (ItemKind a in Items.AttachmentKinds)
                while (_items[(int)a] > 0) Drop(a, 1);
            Drop(ItemKind.Vest1, 0);
            Drop(ItemKind.Helmet1, 0);
            if (BackpackLevel > 0)
            {
                Dropped.Add(new ItemStack(Items.Backpack(BackpackLevel), 1));
                BackpackLevel = 0;
            }
        }
    }
}
