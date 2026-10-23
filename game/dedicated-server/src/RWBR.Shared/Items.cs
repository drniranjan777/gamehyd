using System;
using System.Collections.Generic;

namespace RWBR.Shared
{
    /// <summary>Everything a player can carry or find on the ground. Values are protocol values and must stay stable.</summary>
    public enum ItemKind : byte
    {
        None = 0,
        /// <summary>A gun; the count field holds its <see cref="WeaponCatalog"/> index.</summary>
        Weapon = 1,
        AmmoLight = 2,
        AmmoMedium = 3,
        AmmoShells = 4,
        AmmoHeavy = 5,
        Bandage = 10,
        Medkit = 11,
        EnergyDrink = 12,
        Vest1 = 20,
        Vest2 = 21,
        Vest3 = 22,
        Helmet1 = 24,
        Helmet2 = 25,
        Helmet3 = 26,
        Backpack1 = 28,
        Backpack2 = 29,
        Backpack3 = 30,
        Scope4x = 32,
        ExtendedMag = 33,
        Grip = 34,
        Frag = 40,
        Smoke = 41,
    }

    public enum ItemCategory : byte
    {
        None,
        Weapon,
        Ammo,
        Heal,
        Vest,
        Helmet,
        Backpack,
        Attachment,
        Throwable,
    }

    /// <summary>Weapon attachments, as bits on a weapon slot.</summary>
    [Flags]
    public enum Attachments : byte
    {
        None = 0,
        Scope4x = 1,
        ExtendedMag = 2,
        Grip = 4,
    }

    /// <summary>
    /// Item rules shared by the server, offline matches and the client: names, weight in the backpack, armour and
    /// helmet strength, backpack capacity, heals, ammo calibres and what each attachment does. Balance version 1.
    /// </summary>
    public static class Items
    {
        public const int ItemBalanceVersion = 1;
        /// <summary>Highest <see cref="ItemKind"/> value plus one (array sizes).</summary>
        public const int KindCount = 48;

        /// <summary>Carrying capacity without a backpack, and with backpacks of level 1 to 3.</summary>
        public static readonly int[] Capacity = { 100, 200, 250, 300 };
        /// <summary>Full points of a vest or helmet of level 1 to 3 (index 0 unused).</summary>
        public static readonly int[] ArmorPoints = { 0, 40, 70, 100 };
        /// <summary>Share of body damage a vest absorbs, and of head damage a helmet absorbs, while it has points.</summary>
        public const float VestAbsorb = 0.5f;
        public const float HelmetAbsorb = 0.5f;

        public const float BandageHeal = 15f;
        public const float BandageCap = 75f;
        public const float BandageSeconds = 2.5f;
        public const float MedkitHeal = 75f;
        public const float MedkitSeconds = 5f;
        public const float DrinkSeconds = 2f;
        /// <summary>Health an energy drink restores, a little at a time over <see cref="BoostSeconds"/>.</summary>
        public const float DrinkHeal = 25f;
        public const float BoostSeconds = 10f;

        public static ItemCategory Category(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Weapon: return ItemCategory.Weapon;
                case ItemKind.AmmoLight:
                case ItemKind.AmmoMedium:
                case ItemKind.AmmoShells:
                case ItemKind.AmmoHeavy: return ItemCategory.Ammo;
                case ItemKind.Bandage:
                case ItemKind.Medkit:
                case ItemKind.EnergyDrink: return ItemCategory.Heal;
                case ItemKind.Vest1:
                case ItemKind.Vest2:
                case ItemKind.Vest3: return ItemCategory.Vest;
                case ItemKind.Helmet1:
                case ItemKind.Helmet2:
                case ItemKind.Helmet3: return ItemCategory.Helmet;
                case ItemKind.Backpack1:
                case ItemKind.Backpack2:
                case ItemKind.Backpack3: return ItemCategory.Backpack;
                case ItemKind.Scope4x:
                case ItemKind.ExtendedMag:
                case ItemKind.Grip: return ItemCategory.Attachment;
                case ItemKind.Frag:
                case ItemKind.Smoke: return ItemCategory.Throwable;
                default: return ItemCategory.None;
            }
        }

        /// <summary>Items that sit in the backpack and count towards its weight (ammo, heals, throwables, spare attachments).</summary>
        public static bool Stored(ItemKind kind)
        {
            ItemCategory c = Category(kind);
            return c == ItemCategory.Ammo || c == ItemCategory.Heal || c == ItemCategory.Throwable || c == ItemCategory.Attachment;
        }

        /// <summary>Backpack weight of one unit (one round, one bandage...).</summary>
        public static float Weight(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.AmmoLight: return 0.4f;
                case ItemKind.AmmoMedium: return 0.5f;
                case ItemKind.AmmoShells: return 1.2f;
                case ItemKind.AmmoHeavy: return 0.8f;
                case ItemKind.Bandage: return 2f;
                case ItemKind.Medkit: return 10f;
                case ItemKind.EnergyDrink: return 4f;
                case ItemKind.Frag: return 6f;
                case ItemKind.Smoke: return 5f;
                case ItemKind.Scope4x: return 4f;
                case ItemKind.ExtendedMag: return 3f;
                case ItemKind.Grip: return 3f;
                default: return 0f;
            }
        }

        /// <summary>Level 1 to 3 of a vest, helmet or backpack item; 0 for anything else.</summary>
        public static int Level(ItemKind kind)
        {
            switch (Category(kind))
            {
                case ItemCategory.Vest: return kind - ItemKind.Vest1 + 1;
                case ItemCategory.Helmet: return kind - ItemKind.Helmet1 + 1;
                case ItemCategory.Backpack: return kind - ItemKind.Backpack1 + 1;
                default: return 0;
            }
        }

        public static ItemKind Vest(int level) => level <= 0 ? ItemKind.None : (ItemKind)((int)ItemKind.Vest1 + Math.Min(level, 3) - 1);
        public static ItemKind Helmet(int level) => level <= 0 ? ItemKind.None : (ItemKind)((int)ItemKind.Helmet1 + Math.Min(level, 3) - 1);
        public static ItemKind Backpack(int level) => level <= 0 ? ItemKind.None : (ItemKind)((int)ItemKind.Backpack1 + Math.Min(level, 3) - 1);

        /// <summary>The ammo a weapon class fires.</summary>
        public static ItemKind AmmoFor(WeaponClass c)
        {
            switch (c)
            {
                case WeaponClass.Pistol:
                case WeaponClass.Smg: return ItemKind.AmmoLight;
                case WeaponClass.Shotgun: return ItemKind.AmmoShells;
                case WeaponClass.Sniper: return ItemKind.AmmoHeavy;
                default: return ItemKind.AmmoMedium;
            }
        }

        public static Attachments AttachmentBit(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Scope4x: return Attachments.Scope4x;
                case ItemKind.ExtendedMag: return Attachments.ExtendedMag;
                case ItemKind.Grip: return Attachments.Grip;
                default: return Attachments.None;
            }
        }

        public static readonly ItemKind[] AttachmentKinds = { ItemKind.Scope4x, ItemKind.ExtendedMag, ItemKind.Grip };

        /// <summary>Whether a weapon class takes an attachment: scopes on rifles, SMGs and DMRs; magazines on everything
        /// but shotguns and snipers; grips on SMGs, rifles and DMRs.</summary>
        public static bool Fits(Attachments a, WeaponClass c)
        {
            switch (a)
            {
                case Attachments.Scope4x: return c == WeaponClass.AssaultRifle || c == WeaponClass.Smg || c == WeaponClass.Marksman;
                case Attachments.ExtendedMag: return c != WeaponClass.Shotgun && c != WeaponClass.Sniper;
                case Attachments.Grip: return c == WeaponClass.AssaultRifle || c == WeaponClass.Smg || c == WeaponClass.Marksman;
                default: return false;
            }
        }

        private static readonly Dictionary<(WeaponDefinition, Attachments), WeaponDefinition> Fitted =
            new Dictionary<(WeaponDefinition, Attachments), WeaponDefinition>();

        /// <summary>
        /// A weapon's balance with its attachments: a 4x scope zooms to at least 4x and tightens aimed spread by 20 %,
        /// an extended magazine holds 50 % more, a grip cuts moving spread by 25 % and recoil by 30 %. Cached per pair.
        /// </summary>
        public static WeaponDefinition WithAttachments(WeaponDefinition w, Attachments a)
        {
            if (a == Attachments.None) return w;
            lock (Fitted)
            {
                if (Fitted.TryGetValue((w, a), out WeaponDefinition fitted)) return fitted;
                fitted = new WeaponDefinition
                {
                    Id = w.Id, DisplayName = w.DisplayName, Class = w.Class, BalanceVersion = w.BalanceVersion,
                    Damage = w.Damage, Pellets = w.Pellets, HeadshotMultiplier = w.HeadshotMultiplier, Range = w.Range,
                    FalloffStart = w.FalloffStart, MinDamageFactor = w.MinDamageFactor, FireIntervalSeconds = w.FireIntervalSeconds,
                    MagazineSize = w.MagazineSize, ReloadSeconds = w.ReloadSeconds, SwitchSeconds = w.SwitchSeconds,
                    SpreadDegrees = w.SpreadDegrees, CrouchSpreadMultiplier = w.CrouchSpreadMultiplier,
                    MovingSpreadDegrees = w.MovingSpreadDegrees, AirborneSpreadDegrees = w.AirborneSpreadDegrees,
                    RecoilPitchDegrees = w.RecoilPitchDegrees, RecoilYawDegrees = w.RecoilYawDegrees,
                    AimSpreadMultiplier = w.AimSpreadMultiplier, AimZoom = w.AimZoom,
                };
                if ((a & Attachments.Scope4x) != 0 && Fits(Attachments.Scope4x, w.Class))
                {
                    fitted.AimZoom = Math.Max(fitted.AimZoom, 4f);
                    fitted.AimSpreadMultiplier *= 0.8f;
                }
                if ((a & Attachments.ExtendedMag) != 0 && Fits(Attachments.ExtendedMag, w.Class))
                    fitted.MagazineSize = (int)Math.Ceiling(w.MagazineSize * 1.5f);
                if ((a & Attachments.Grip) != 0 && Fits(Attachments.Grip, w.Class))
                {
                    fitted.MovingSpreadDegrees *= 0.75f;
                    fitted.RecoilPitchDegrees *= 0.7f;
                    fitted.RecoilYawDegrees *= 0.7f;
                }
                Fitted[(w, a)] = fitted;
                return fitted;
            }
        }

        public static string Name(ItemKind kind, int count = 0)
        {
            switch (kind)
            {
                case ItemKind.Weapon: return WeaponCatalog.IsValid(count) ? WeaponCatalog.Get(count).DisplayName : "Weapon";
                case ItemKind.AmmoLight: return "Light ammo";
                case ItemKind.AmmoMedium: return "Medium ammo";
                case ItemKind.AmmoShells: return "Shells";
                case ItemKind.AmmoHeavy: return "Heavy ammo";
                case ItemKind.Bandage: return "Bandage";
                case ItemKind.Medkit: return "Medkit";
                case ItemKind.EnergyDrink: return "Energy drink";
                case ItemKind.Scope4x: return "4x scope";
                case ItemKind.ExtendedMag: return "Extended magazine";
                case ItemKind.Grip: return "Grip";
                case ItemKind.Frag: return "Frag grenade";
                case ItemKind.Smoke: return "Smoke grenade";
            }
            switch (Category(kind))
            {
                case ItemCategory.Vest: return $"Vest (level {Level(kind)})";
                case ItemCategory.Helmet: return $"Helmet (level {Level(kind)})";
                case ItemCategory.Backpack: return $"Backpack (level {Level(kind)})";
                default: return kind.ToString();
            }
        }

        /// <summary>A ground item's label: "Light ammo ×30", "Helmet (level 2)", "Standard Rifle".</summary>
        public static string Label(ItemKind kind, int count) =>
            Stored(kind) && count > 1 ? $"{Name(kind)} ×{count}" : Name(kind, count);
    }
}

namespace RWBR.Shared
{
    /// <summary>A pile of one item: what goes on the ground when something is dropped or a player is eliminated.</summary>
    public struct ItemStack
    {
        public ItemKind Kind;
        /// <summary>Units (rounds, heals, armour points); the catalog index for a weapon.</summary>
        public int Count;
        /// <summary>A dropped weapon's magazine and attachments.</summary>
        public int Ammo;
        public Attachments Attach;

        public ItemStack(ItemKind kind, int count, int ammo = 0, Attachments attach = Attachments.None)
        {
            Kind = kind;
            Count = count;
            Ammo = ammo;
            Attach = attach;
        }
    }
}
