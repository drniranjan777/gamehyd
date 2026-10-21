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
        public WeaponDefinition Weapon { get; set; } = WeaponDefinition.DefaultRifle();
        public MapLayout Map { get; set; } = MapLayout.GreyboxBlock(100f);

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
