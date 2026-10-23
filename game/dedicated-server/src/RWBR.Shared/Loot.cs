using System.Numerics;

namespace RWBR.Shared
{
    /// <summary>
    /// The kind of loot spot a map generator placed. Published maps pin these (with their quantities); what each spot
    /// actually holds is decided by <see cref="LootTable"/>, so items can change without republishing a map.
    /// </summary>
    public enum LootType : byte
    {
        /// <summary>The standard rifle (greybox maps; same as Weapon with the rifle's catalog index).</summary>
        Rifle = 1,
        Ammo = 2,
        Medkit = 3,
        Armor = 4,
        /// <summary>Any catalog weapon; <see cref="LootSpawn.Quantity"/> is the WeaponCatalog index.</summary>
        Weapon = 5,
    }

    /// <summary>A fixed loot spawn in a map version. Ids are the index in <see cref="MapLayout.Loot"/>.</summary>
    public readonly struct LootSpawn
    {
        /// <summary>A spot holding the simple loot for its type (see <see cref="LootTable.Simple"/>).</summary>
        public LootSpawn(ushort id, LootType type, Vector3 position, int quantity)
        {
            Id = id;
            Type = type;
            Position = position;
            Quantity = quantity;
            LootSpawn simple = LootTable.Simple(new LootSpawn(id, type, position, quantity, ItemKind.None, 0));
            Kind = simple.Kind;
            Count = simple.Count;
        }

        private LootSpawn(ushort id, LootType type, Vector3 position, int quantity, ItemKind kind, int count)
        {
            Id = id;
            Type = type;
            Position = position;
            Quantity = quantity;
            Kind = kind;
            Count = count;
        }

        public ushort Id { get; }
        /// <summary>The spot as the map generator placed it (pinned for published maps).</summary>
        public LootType Type { get; }
        public Vector3 Position { get; }
        /// <summary>Rounds for ammo, armor points for armor, count for medkits, catalog index for weapons.</summary>
        public int Quantity { get; }

        /// <summary>The item the loot table put here.</summary>
        public ItemKind Kind { get; }
        /// <summary>Units of <see cref="Kind"/>: rounds, heals, armour points; the catalog index for weapons.</summary>
        public int Count { get; }

        public LootSpawn WithItem(ItemKind kind, int count) => new LootSpawn(Id, Type, Position, Quantity, kind, count);

        /// <summary>Catalog index if this is a weapon, else -1.</summary>
        public int WeaponIndex => Kind == ItemKind.Weapon && WeaponCatalog.IsValid(Count) ? Count : -1;

        public string DisplayName => Items.Label(Kind, Count);
    }

    /// <summary>
    /// What each loot spot holds (loot table version 1). Deterministic from the map version and the spot's id, so the
    /// server and every client agree without sending it. Weapons stay where the map put them. Ammo spots hold one of
    /// the four calibres, and some hold throwables or attachments instead. Medkit spots hold bandages, medkits or energy
    /// drinks. Armour spots hold a vest, helmet or backpack, stronger where the map placed stronger armour.
    /// Small maps (the training ground and range) keep their simple loot (<see cref="Simple"/>).
    /// </summary>
    public static class LootTable
    {
        public const int Version = 1;

        /// <summary>
        /// Simple loot (small maps, and spots built without a map): rifle ammo, medkits and a vest whose level follows the
        /// spot's armour points (50 and below level 1, under 100 level 2, 100 level 3).
        /// </summary>
        public static LootSpawn Simple(LootSpawn spot)
        {
            switch (spot.Type)
            {
                case LootType.Rifle: return spot.WithItem(ItemKind.Weapon, WeaponCatalog.Rifle);
                case LootType.Weapon: return spot.WithItem(ItemKind.Weapon, spot.Quantity);
                case LootType.Medkit: return spot.WithItem(ItemKind.Medkit, spot.Quantity);
                case LootType.Armor:
                    int level = spot.Quantity >= 100 ? 3 : spot.Quantity > 50 ? 2 : 1;
                    return spot.WithItem(Items.Vest(level), Items.ArmorPoints[level]);
                default: return spot.WithItem(ItemKind.AmmoMedium, spot.Quantity);
            }
        }

        public static LootSpawn Resolve(string mapVersion, LootSpawn spot)
        {
            if (!mapVersion.StartsWith("hyd-city", System.StringComparison.Ordinal)) return Simple(spot);
            switch (spot.Type)
            {
                case LootType.Rifle:
                    return spot.WithItem(ItemKind.Weapon, WeaponCatalog.Rifle);
                case LootType.Weapon:
                    return spot.WithItem(ItemKind.Weapon, spot.Quantity);
                case LootType.Medkit:
                    {
                        float r = Roll(spot.Id, 1);
                        if (r < 0.5f) return spot.WithItem(ItemKind.Bandage, 3);
                        return r < 0.8f ? spot.WithItem(ItemKind.Medkit, 1) : spot.WithItem(ItemKind.EnergyDrink, 1);
                    }
                case LootType.Armor:
                    {
                        // Strong spots (100) give level 2 or 3, weaker ones (50) level 1 or 2.
                        int level = (spot.Quantity >= 100 ? 2 : 1) + (Roll(spot.Id, 2) < 0.4f ? 1 : 0);
                        float r = Roll(spot.Id, 3);
                        if (r < 0.4f) return spot.WithItem(Items.Vest(level), Items.ArmorPoints[level]);
                        if (r < 0.75f) return spot.WithItem(Items.Helmet(level), Items.ArmorPoints[level]);
                        return spot.WithItem(Items.Backpack(level), 1);
                    }
                default: // Ammo
                    {
                        float r = Roll(spot.Id, 4);
                        if (r < 0.1f) return spot.WithItem(ItemKind.Frag, 1);
                        if (r < 0.15f) return spot.WithItem(ItemKind.Smoke, 1);
                        if (r < 0.3f) return spot.WithItem(Items.AttachmentKinds[(int)(Roll(spot.Id, 5) * 3) % 3], 1);
                        float c = Roll(spot.Id, 6);
                        if (c < 0.35f) return spot.WithItem(ItemKind.AmmoLight, 45);
                        if (c < 0.7f) return spot.WithItem(ItemKind.AmmoMedium, 45);
                        return c < 0.85f ? spot.WithItem(ItemKind.AmmoShells, 15) : spot.WithItem(ItemKind.AmmoHeavy, 15);
                    }
            }
        }

        /// <summary>A stable pseudo-random number in [0, 1) for a spot and a purpose.</summary>
        private static float Roll(ushort id, uint salt)
        {
            uint h = (id + 1u) * 2654435761u ^ (salt * 0x9E3779B9u);
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
