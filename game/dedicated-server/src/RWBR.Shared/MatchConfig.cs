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
        /// <summary>Each later zone stage hurts this much more (0.5: stage 2 does 1.5x, stage 3 2x...).</summary>
        public float ZoneDamageGrowth { get; set; } = 0.5f;
        /// <summary>
        /// Picks where each circle closes. 0 keeps every circle centred on the map (tests, old behaviour); any other
        /// value moves each new circle to a random spot inside the previous one, the same for everyone in the match.
        /// </summary>
        public int ZoneSeed { get; set; }
        /// <summary>
        /// When the countdown starts, bots fill the match up to this many players (0 = no bots). They play through the
        /// same packets as everyone else and are left out of the platform result except as a count.
        /// </summary>
        public int FillBotsTo { get; set; }
        /// <summary>Start from a plane flying across the map (jump, fall, parachute) instead of the map's spawn points.</summary>
        public bool PlaneDrop { get; set; }
        /// <summary>Players per team: 1 Solo, 2 Duo, 4 Squad. Teammates can't hurt each other and win or place together.</summary>
        public int TeamSize { get; set; } = 1;
        /// <summary>
        /// Teams fixed by the platform (account → team number) for parties; anyone not listed, and every player without
        /// a ticket, is placed in order, filling incomplete teams first.
        /// </summary>
        public Dictionary<System.Guid, int>? Teams { get; set; }
        public Bots.BotDifficulty BotDifficulty { get; set; } = Bots.BotDifficulty.Normal;
        /// <summary>Cars at the map's vehicle spawns (<see cref="VehicleSpawns"/>; the small maps have none).</summary>
        public bool Vehicles { get; set; } = true;
        /// <summary>Live-ops loot weights; sent to every client in Welcome.</summary>
        public LootTableConfig LootTable { get; set; } = LootTableConfig.Default;
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

        /// <summary>Weapons switched off by the live-ops kill switch: no loot of them spawns and nobody starts with one.</summary>
        public HashSet<int> DisabledWeapons { get; } = new HashSet<int>();

        /// <summary>Disables weapons by catalog id; unknown ids are returned and ignored. Picks another starting weapon if needed.</summary>
        public List<string> DisableWeapons(IEnumerable<string> ids)
        {
            var unknown = new List<string>();
            foreach (string id in ids)
            {
                int index = WeaponCatalog.IndexOf(id);
                if (index < 0) unknown.Add(id);
                else DisabledWeapons.Add(index);
            }
            if (StartWithWeapon && DisabledWeapons.Contains(StartingWeaponIndex))
            {
                int[] preference = { WeaponCatalog.Rifle, WeaponCatalog.Smg, WeaponCatalog.Pistol, WeaponCatalog.Marksman, WeaponCatalog.Shotgun, WeaponCatalog.Sniper };
                int next = -1;
                foreach (int w in preference)
                    if (!DisabledWeapons.Contains(w)) { next = w; break; }
                if (next < 0) StartWithWeapon = false;
                else
                {
                    StartingWeaponIndex = next;
                    Weapon = WeaponCatalog.Get(next);
                }
            }
            return unknown;
        }
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
