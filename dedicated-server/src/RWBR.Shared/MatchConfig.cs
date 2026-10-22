#nullable enable
using System.Collections.Generic;

namespace RWBR.Shared
{
    public sealed class ZoneStage
    {
        public ZoneStage(float holdSeconds, float shrinkSeconds, float endRadius)
        {
            HoldSeconds = holdSeconds;
            ShrinkSeconds = shrinkSeconds;
            EndRadius = endRadius;
        }

        public float HoldSeconds { get; }
        public float ShrinkSeconds { get; }
        public float EndRadius { get; }
    }

    /// <summary>All match timing lives here, never scattered through client code (BRD 5.1).</summary>
    public sealed class MatchConfig
    {
        public int TickRate { get; set; } = 30;
        public int MinPlayers { get; set; } = 2;
        public int MaxPlayers { get; set; } = 30;
        public float CountdownSeconds { get; set; } = 5f;
        /// <summary>
        /// Once MinPlayers are connected, wait this long for the match to fill before counting down
        /// (0 = start as soon as MinPlayers are in). A full match always starts immediately.
        /// </summary>
        public float FillWaitSeconds { get; set; }
        public float MaxMatchSeconds { get; set; } = 900f;
        public float ZoneDamagePerSecond { get; set; } = 5f;
        public List<ZoneStage> ZoneStages { get; set; } = new List<ZoneStage>
        {
            new ZoneStage(60, 60, 60),
            new ZoneStage(45, 45, 30),
            new ZoneStage(30, 30, 10),
            new ZoneStage(20, 20, 0),
        };
        public float MaxRewindSeconds { get; set; } = 0.2f;
        public float DisconnectTimeoutSeconds { get; set; } = 3f;
        public float ReconnectGraceSeconds { get; set; } = 20f;
        public float ResultBroadcastSeconds { get; set; } = 1f;
        /// <summary>Balance for the starting weapon; every other weapon comes from <see cref="WeaponCatalog"/>.</summary>
        public WeaponDefinition Weapon { get; set; } = WeaponCatalog.Get(WeaponCatalog.Rifle);
        public int StartingWeaponIndex { get; set; } = WeaponCatalog.Rifle;

        /// <summary>Balance for a catalog index (the starting weapon may be overridden, e.g. by tests).</summary>
        public WeaponDefinition WeaponDef(int index) =>
            index == StartingWeaponIndex ? Weapon : WeaponCatalog.IsValid(index) ? WeaponCatalog.Get(index) : Weapon;
        public MapLayout Map { get; set; } = MapLayout.GreyboxBlock(100f);
        /// <summary>Prototype loadout: players spawn armed until the drop/loot design is signed off (Appendix A).</summary>
        public bool StartWithWeapon { get; set; } = true;
        public int StartingReserveAmmo { get; set; } = 90;
        /// <summary>Send a snapshot every N ticks (1 = every tick). Clients interpolate between them.</summary>
        public int SnapshotEveryTicks { get; set; } = 1;
        /// <summary>HMAC secret shared with the platform API. Null = open dev server without join tickets.</summary>
        public byte[]? TicketSecret { get; set; }
        /// <summary>The match these tickets must name (ticket mode).</summary>
        public System.Guid MatchId { get; set; }

        /// <summary>
        /// Production zone schedule for a map: the greybox block keeps its original timings; larger maps get
        /// proportionally sized circles and longer holds (city: about 8 minutes from start to the final circle).
        /// </summary>
        public static List<ZoneStage> ZonesFor(MapLayout map)
        {
            if (map.HalfSize <= 150) return new MatchConfig().ZoneStages;
            float h = map.HalfSize;
            return new List<ZoneStage>
            {
                new ZoneStage(90, 75, h * 0.5f),
                new ZoneStage(60, 60, h * 0.25f),
                new ZoneStage(45, 45, h * 0.1f),
                new ZoneStage(30, 30, 0),
            };
        }

        public int Ticks(float seconds) => (int)System.Math.Ceiling(seconds * TickRate);

        /// <summary>Short match on a small map: used by automated tests and quick bot runs.</summary>
        public static MatchConfig Fast(int players) => new MatchConfig
        {
            MinPlayers = players,
            MaxPlayers = players,
            CountdownSeconds = 1f,
            MaxMatchSeconds = 60f,
            ZoneDamagePerSecond = 25f,
            ZoneStages = new List<ZoneStage> { new ZoneStage(2, 6, 15), new ZoneStage(1, 4, 0) },
            ReconnectGraceSeconds = 5f,
            ResultBroadcastSeconds = 0.5f,
            Weapon = new WeaponDefinition { Damage = 25f, FireIntervalSeconds = 0.1f, ReloadSeconds = 1.5f },
            Map = MapLayout.GreyboxBlock(40f),
        };
    }
}
