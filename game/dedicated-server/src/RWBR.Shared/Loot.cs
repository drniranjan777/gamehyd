using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using RWBR.Shared.Protocol;

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
    /// The live-ops loot weights (admin portal → game config → match assignment → server → every client in Welcome).
    /// Shares are whole percents of the spots of that kind; whatever is left goes to the last option (heavy ammo,
    /// energy drinks, backpacks). Out-of-range values are clamped, never rejected, so a bad edit can't stop matches.
    /// </summary>
    public sealed class LootTableConfig
    {
        public static readonly LootTableConfig Default = new LootTableConfig();

        /// <summary>Raised by live ops on every change, for the audit trail and analytics.</summary>
        public int Version { get; set; } = 1;
        // Ammo spots: grenades and attachments first, the rest ammo.
        public int FragPercent { get; set; } = 10;
        public int SmokePercent { get; set; } = 5;
        public int AttachmentPercent { get; set; } = 15;
        // Of the ammo: light, medium, shells; the rest heavy.
        public int LightPercent { get; set; } = 35;
        public int MediumPercent { get; set; } = 35;
        public int ShellsPercent { get; set; } = 15;
        public int LightRounds { get; set; } = 45;
        public int MediumRounds { get; set; } = 45;
        public int ShellRounds { get; set; } = 15;
        public int HeavyRounds { get; set; } = 15;
        // Heal spots: bandages, medkits; the rest energy drinks.
        public int BandagePercent { get; set; } = 50;
        public int MedkitPercent { get; set; } = 30;
        public int BandagesPerSpot { get; set; } = 3;
        // Armour spots: vests, helmets; the rest backpacks. Chance of one level higher than the spot's base.
        public int VestPercent { get; set; } = 40;
        public int HelmetPercent { get; set; } = 35;
        public int UpgradePercent { get; set; } = 40;

        /// <summary>A copy with every value in range and every group of shares adding up to at most 100.</summary>
        public LootTableConfig Clamped()
        {
            static int P(int v) => Math.Clamp(v, 0, 100);
            static int R(int v) => Math.Clamp(v, 1, 200);
            var c = new LootTableConfig
            {
                Version = Math.Clamp(Version, 1, ushort.MaxValue),
                FragPercent = P(FragPercent), SmokePercent = P(SmokePercent), AttachmentPercent = P(AttachmentPercent),
                LightPercent = P(LightPercent), MediumPercent = P(MediumPercent), ShellsPercent = P(ShellsPercent),
                LightRounds = R(LightRounds), MediumRounds = R(MediumRounds), ShellRounds = R(ShellRounds), HeavyRounds = R(HeavyRounds),
                BandagePercent = P(BandagePercent), MedkitPercent = P(MedkitPercent), BandagesPerSpot = Math.Clamp(BandagesPerSpot, 1, 10),
                VestPercent = P(VestPercent), HelmetPercent = P(HelmetPercent), UpgradePercent = P(UpgradePercent),
            };
            c.SmokePercent = Math.Min(c.SmokePercent, 100 - c.FragPercent);
            c.AttachmentPercent = Math.Min(c.AttachmentPercent, 100 - c.FragPercent - c.SmokePercent);
            c.MediumPercent = Math.Min(c.MediumPercent, 100 - c.LightPercent);
            c.ShellsPercent = Math.Min(c.ShellsPercent, 100 - c.LightPercent - c.MediumPercent);
            c.MedkitPercent = Math.Min(c.MedkitPercent, 100 - c.BandagePercent);
            c.HelmetPercent = Math.Min(c.HelmetPercent, 100 - c.VestPercent);
            return c;
        }

        /// <summary>Identifies the weights (not the version): two configs with the same key give the same loot.</summary>
        public string Key =>
            $"{FragPercent},{SmokePercent},{AttachmentPercent},{LightPercent},{MediumPercent},{ShellsPercent},{LightRounds},{MediumRounds},{ShellRounds},{HeavyRounds}," +
            $"{BandagePercent},{MedkitPercent},{BandagesPerSpot},{VestPercent},{HelmetPercent},{UpgradePercent}";

        public void Write(PacketWriter w)
        {
            w.U16((ushort)Version);
            foreach (int v in Values()) w.U8((byte)v);
        }

        public static LootTableConfig Read(PacketReader r)
        {
            var c = new LootTableConfig { Version = r.U16() };
            c.FragPercent = r.U8();
            c.SmokePercent = r.U8();
            c.AttachmentPercent = r.U8();
            c.LightPercent = r.U8();
            c.MediumPercent = r.U8();
            c.ShellsPercent = r.U8();
            c.LightRounds = r.U8();
            c.MediumRounds = r.U8();
            c.ShellRounds = r.U8();
            c.HeavyRounds = r.U8();
            c.BandagePercent = r.U8();
            c.MedkitPercent = r.U8();
            c.BandagesPerSpot = r.U8();
            c.VestPercent = r.U8();
            c.HelmetPercent = r.U8();
            c.UpgradePercent = r.U8();
            return c.Clamped();
        }

        private IEnumerable<int> Values()
        {
            yield return FragPercent;
            yield return SmokePercent;
            yield return AttachmentPercent;
            yield return LightPercent;
            yield return MediumPercent;
            yield return ShellsPercent;
            yield return LightRounds;
            yield return MediumRounds;
            yield return ShellRounds;
            yield return HeavyRounds;
            yield return BandagePercent;
            yield return MedkitPercent;
            yield return BandagesPerSpot;
            yield return VestPercent;
            yield return HelmetPercent;
            yield return UpgradePercent;
        }
    }

    /// <summary>
    /// What each loot spot holds. Deterministic from the map version, the spot's id and the
    /// <see cref="LootTableConfig"/>, so the server and every client agree with only the config sent. Weapons stay where
    /// the map put them. Ammo spots hold one of the four calibres, or a grenade or attachment instead. Medkit spots hold
    /// bandages, medkits or energy drinks. Armour spots hold a vest, helmet or backpack, stronger where the map placed
    /// stronger armour. Small maps (the training ground and range) keep their simple loot (<see cref="Simple"/>).
    /// </summary>
    public static class LootTable
    {
        public const int Version = 1;

        private static readonly ConditionalWeakTable<MapLayout, Dictionary<string, LootSpawn[]>> Applied =
            new ConditionalWeakTable<MapLayout, Dictionary<string, LootSpawn[]>>();

        /// <summary>The map's loot spots with items from <paramref name="config"/> (cached per map and weights).</summary>
        public static IReadOnlyList<LootSpawn> Apply(MapLayout map, LootTableConfig? config)
        {
            if (config == null || config.Key == LootTableConfig.Default.Key) return map.Loot;
            LootTableConfig c = config.Clamped();
            Dictionary<string, LootSpawn[]> byKey = Applied.GetValue(map, _ => new Dictionary<string, LootSpawn[]>());
            lock (byKey)
            {
                if (byKey.TryGetValue(c.Key, out LootSpawn[] cached)) return cached;
                var spots = new LootSpawn[map.Loot.Count];
                for (int i = 0; i < spots.Length; i++) spots[i] = Resolve(map.Version, map.Loot[i], c);
                byKey[c.Key] = spots;
                return spots;
            }
        }

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

        public static LootSpawn Resolve(string mapVersion, LootSpawn spot, LootTableConfig? config = null)
        {
            if (!mapVersion.StartsWith("hyd-city", System.StringComparison.Ordinal)) return Simple(spot);
            LootTableConfig t = config ?? LootTableConfig.Default;
            switch (spot.Type)
            {
                case LootType.Rifle:
                    return spot.WithItem(ItemKind.Weapon, WeaponCatalog.Rifle);
                case LootType.Weapon:
                    return spot.WithItem(ItemKind.Weapon, spot.Quantity);
                case LootType.Medkit:
                    {
                        float r = Roll(spot.Id, 1) * 100;
                        if (r < t.BandagePercent) return spot.WithItem(ItemKind.Bandage, t.BandagesPerSpot);
                        return r < t.BandagePercent + t.MedkitPercent ? spot.WithItem(ItemKind.Medkit, 1) : spot.WithItem(ItemKind.EnergyDrink, 1);
                    }
                case LootType.Armor:
                    {
                        // Strong spots (100) give level 2 or 3, weaker ones (50) level 1 or 2.
                        int level = (spot.Quantity >= 100 ? 2 : 1) + (Roll(spot.Id, 2) * 100 < t.UpgradePercent ? 1 : 0);
                        float r = Roll(spot.Id, 3) * 100;
                        if (r < t.VestPercent) return spot.WithItem(Items.Vest(level), Items.ArmorPoints[level]);
                        if (r < t.VestPercent + t.HelmetPercent) return spot.WithItem(Items.Helmet(level), Items.ArmorPoints[level]);
                        return spot.WithItem(Items.Backpack(level), 1);
                    }
                default: // Ammo
                    {
                        float r = Roll(spot.Id, 4) * 100;
                        if (r < t.FragPercent) return spot.WithItem(ItemKind.Frag, 1);
                        if (r < t.FragPercent + t.SmokePercent) return spot.WithItem(ItemKind.Smoke, 1);
                        if (r < t.FragPercent + t.SmokePercent + t.AttachmentPercent)
                            return spot.WithItem(Items.AttachmentKinds[(int)(Roll(spot.Id, 5) * 3) % 3], 1);
                        float c = Roll(spot.Id, 6) * 100;
                        if (c < t.LightPercent) return spot.WithItem(ItemKind.AmmoLight, t.LightRounds);
                        if (c < t.LightPercent + t.MediumPercent) return spot.WithItem(ItemKind.AmmoMedium, t.MediumRounds);
                        return c < t.LightPercent + t.MediumPercent + t.ShellsPercent
                            ? spot.WithItem(ItemKind.AmmoShells, t.ShellRounds)
                            : spot.WithItem(ItemKind.AmmoHeavy, t.HeavyRounds);
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
