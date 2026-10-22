using System.Numerics;

namespace RWBR.Shared
{
    public enum LootType : byte
    {
        Rifle = 1,
        Ammo = 2,
        Medkit = 3,
        Armor = 4,
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
        /// <summary>Rounds for ammo, armor points for armor, count for medkits.</summary>
        public int Quantity { get; }

        public string DisplayName => Type switch
        {
            LootType.Rifle => "Rifle",
            LootType.Ammo => $"Ammo ×{Quantity}",
            LootType.Medkit => Quantity > 1 ? $"Medkit ×{Quantity}" : "Medkit",
            LootType.Armor => $"Armor vest ({Quantity})",
            _ => Type.ToString(),
        };
    }
}
