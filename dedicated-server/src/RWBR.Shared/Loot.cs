using System.Numerics;

namespace RWBR.Shared
{
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
        public LootSpawn(ushort id, LootType type, Vector3 position, int quantity)
        {
            Id = id;
            Type = type;
            Position = position;
            Quantity = quantity;
        }

        public ushort Id { get; }
        public LootType Type { get; }
        public Vector3 Position { get; }
        /// <summary>Rounds for ammo, armor points for armor, count for medkits, catalog index for weapons.</summary>
        public int Quantity { get; }

        /// <summary>Catalog index if this is a weapon, else -1.</summary>
        public int WeaponIndex => Type == LootType.Rifle ? WeaponCatalog.Rifle
            : Type == LootType.Weapon && WeaponCatalog.IsValid(Quantity) ? Quantity : -1;

        public string DisplayName => Type switch
        {
            LootType.Rifle => WeaponCatalog.Get(WeaponCatalog.Rifle).DisplayName,
            LootType.Weapon => WeaponCatalog.IsValid(Quantity) ? WeaponCatalog.Get(Quantity).DisplayName : "Weapon",
            LootType.Ammo => $"Ammo ×{Quantity}",
            LootType.Medkit => Quantity > 1 ? $"Medkit ×{Quantity}" : "Medkit",
            LootType.Armor => $"Armor vest ({Quantity})",
            _ => Type.ToString(),
        };
    }
}
