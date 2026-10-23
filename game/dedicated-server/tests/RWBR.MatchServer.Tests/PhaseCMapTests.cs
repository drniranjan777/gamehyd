using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using RWBR.Shared;
using RWBR.Shared.Bots;
using RWBR.Shared.Server;
using Xunit;
using Xunit.Abstractions;

namespace RWBR.MatchServer.Tests;

/// <summary>Phase C part 3: hyd-city-v2, the 900 m map with the industrial, riverside, outskirts and logistics ring.</summary>
public class CityV2Tests
{
    private readonly ITestOutputHelper _out;
    private static readonly MapLayout V1 = CityMap.Build();
    private static readonly MapLayout V2 = CityMap.BuildV2();

    public CityV2Tests(ITestOutputHelper output) => _out = output;

    private static string Fingerprint(MapLayout map)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < map.Obstacles.Count; i++)
        {
            Aabb b = map.Obstacles[i];
            sb.Append(FormattableString.Invariant($"{b.Min.X:F3},{b.Min.Y:F3},{b.Min.Z:F3},{b.Max.X:F3},{b.Max.Y:F3},{b.Max.Z:F3},{(int)map.KindOf(i)};"));
        }
        foreach (LootSpawn l in map.Loot) sb.Append(FormattableString.Invariant($"{l.Type},{l.Position.X:F3},{l.Position.Y:F3},{l.Position.Z:F3},{l.Quantity};"));
        foreach (Vector3 s in map.Spawns) sb.Append(FormattableString.Invariant($"{s.X:F1},{s.Z:F1};"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>hyd-city-v2 is published (migration 0011): its geometry and loot spots must never change.</summary>
    [Fact]
    public void Published_v2_geometry_is_pinned()
    {
        string hash = Fingerprint(V2);
        _out.WriteLine($"{V2.Obstacles.Count} boxes, {V2.Loot.Count} loot, {V2.Spawns.Count} spawns, sha256 {hash}");
        Assert.Equal(PinnedHash, hash);
    }

    private const string PinnedHash = "A21380AD94FE8E45BDACE09661E38C770303A984E85DA3C271E86522E313D2D3";

    [Fact]
    public void V2_keeps_v1_at_its_centre_and_adds_the_ring_districts()
    {
        Assert.Equal(CityMap.VersionV2, V2.Version);
        Assert.Equal(450f, V2.HalfSize);
        for (int i = 0; i < V1.Obstacles.Count; i++) Assert.Equal(V1.Obstacles[i], V2.Obstacles[i]);
        Assert.True(MapLayout.TryFromVersion("hyd-city-v2", out MapLayout same) && ReferenceEquals(same, V2));
        Assert.Equal(9, V2.Districts.Count);
        Assert.Equal("Industrial Estate", V2.DistrictAt(0, 400));
        Assert.Equal("Musi Riverside", V2.DistrictAt(400, 0));
        Assert.Equal("Outskirts", V2.DistrictAt(0, -400));
        Assert.Equal("Logistics Park", V2.DistrictAt(-400, 0));
        Assert.Equal("Char Gate Plaza", V2.DistrictAt(0, 0));
        foreach (BoxKind k in new[] { BoxKind.Warehouse, BoxKind.Container, BoxKind.Rock, BoxKind.Chimney, BoxKind.Bridge, BoxKind.Water })
            Assert.Contains(Enumerable.Range(0, V2.Obstacles.Count), i => V2.KindOf(i) == k);
        int ringLoot = V2.Loot.Count(l => Math.Abs(l.Position.X) > 300 || Math.Abs(l.Position.Z) > 300);
        _out.WriteLine($"{V2.Obstacles.Count} boxes ({V2.Obstacles.Count - V1.Obstacles.Count} new), {V2.Loot.Count} loot ({ringLoot} in the ring)");
        Assert.True(ringLoot > 60, $"only {ringLoot} loot spots in the ring");
    }

    [Fact]
    public void Spawns_are_clear_and_ring_loot_is_not_buried()
    {
        foreach (Vector3 s in V2.Spawns)
        {
            Assert.Equal(s, Simulation.ResolveCollisions(s, V2));
            Assert.Equal(0f, Simulation.GroundHeight(s, s.Y + GameRules.StepHeight, V2));
        }
        var buried = V2.Loot.Where(l => Simulation.ResolveHorizontal(l.Position, l.Position.Y, 0.5f, V2) != l.Position &&
                                        !Simulation.HasHeadroom(l.Position, 0.5f, V2)).ToList();
        foreach (LootSpawn l in buried) _out.WriteLine($"buried {l.Kind} at {l.Position} in {V2.DistrictAt(l.Position.X, l.Position.Z)}");
        Assert.Empty(buried);
    }

    [Fact]
    public void The_river_is_crossed_only_on_the_bridges()
    {
        NavGrid nav = NavGrid.For(V2);
        var west = new Vector3(CityMap.RiverWest - 10, 0, 100);
        var east = new Vector3(CityMap.RiverEast + 10, 0, 100);
        List<Vector3>? path = nav.FindPath(west, east);
        Assert.NotNull(path);
        // Every point where the path is over the river is on a bridge.
        Vector3 prev = west;
        foreach (Vector3 wp in path!)
        {
            for (float t = 0; t <= 1; t += 0.05f)
            {
                Vector3 p = Vector3.Lerp(prev, wp, t);
                if (p.X > CityMap.RiverWest + 0.5f && p.X < CityMap.RiverEast - 0.5f)
                    Assert.Contains(CityMap.BridgeZs, z => Math.Abs(p.Z - z) < 7f);
            }
            prev = wp;
        }
        // Walking (and jumping) straight east into the water stops at the bank.
        MotorState m = MotorState.StandingAt(new Vector3(CityMap.RiverWest - 3, 0, 100));
        for (int i = 0; i < 30 * 6; i++)
            Simulation.Step(ref m, new InputCommand { MoveZ = 1, Yaw = MathF.PI / 2, Buttons = i % 20 == 0 ? InputButtons.Jump | InputButtons.Sprint : InputButtons.Sprint }, 1f / 30, V2);
        Assert.True(m.Position.X < CityMap.RiverWest, $"walked into the river to {m.Position}");
    }

    [Fact]
    public void Ring_loot_is_reachable_on_foot_and_warehouses_can_be_entered()
    {
        NavGrid nav = NavGrid.For(V2);
        var ground = V2.Loot.Where(l => l.Position.Y < 0.5f && (Math.Abs(l.Position.X) > 300 || Math.Abs(l.Position.Z) > 300)).ToList();
        int reachable = ground.Count(l => nav.IsWalkable(l.Position) && nav.FindPath(Vector3.Zero, l.Position) != null);
        _out.WriteLine($"{reachable} of {ground.Count} ground loot spots in the ring reachable from the centre");
        Assert.True(reachable >= ground.Count * 0.9, $"only {reachable} of {ground.Count} reachable");

        // A warehouse: loot on its centre line is inside walls 7 m high, and a path goes in through a doorway.
        int warehouseWall = Enumerable.Range(0, V2.Obstacles.Count).First(i => V2.KindOf(i) == BoxKind.Warehouse);
        Aabb wall = V2.Obstacles[warehouseWall];
        Assert.Equal(7f, wall.Max.Y, 3);
    }

    [Fact]
    public void Loot_table_gives_the_ring_the_same_item_mix()
    {
        var kinds = V2.Loot.Where(l => Math.Abs(l.Position.Z) > 300).Select(l => l.Kind).ToHashSet();
        Assert.Contains(ItemKind.Weapon, kinds);
        Assert.Contains(ItemKind.Frag, kinds.Concat(V2.Loot.Select(l => l.Kind)));
        Assert.Contains(V2.Loot, l => l.WeaponIndex == WeaponCatalog.Sniper && l.Position.Z < -300); // long range in the open
    }
}

public class MovingZoneTests
{
    private static Match Started(int seed, MapLayout map, out List<System.Net.IPEndPoint> clients)
    {
        var cfg = new MatchConfig
        {
            MinPlayers = 2, MaxPlayers = 2, Map = map, CountdownSeconds = 0.1f, ZoneSeed = seed,
            ZoneStages = MatchConfig.ZonesFor(map), MaxMatchSeconds = 2000,
        };
        var m = new Match(cfg);
        clients = new List<System.Net.IPEndPoint> { new(System.Net.IPAddress.Loopback, 9300), new(System.Net.IPAddress.Loopback, 9301) };
        foreach (var c in clients)
        {
            byte[] hello = RWBR.Shared.Protocol.PacketCodec.Encode(new RWBR.Shared.Protocol.HelloPacket { Name = "z" });
            m.Receive(c, hello, hello.Length);
        }
        uint seq = 0;
        while (m.Phase != RWBR.Shared.Protocol.MatchPhase.Active) Step(m, clients, ref seq);
        return m;
    }

    private static void Step(Match m, List<System.Net.IPEndPoint> clients, ref uint seq)
    {
        seq++;
        foreach (var c in clients)
        {
            byte[] data = RWBR.Shared.Protocol.PacketCodec.Encode(new List<InputCommand> { new() { Sequence = seq } });
            m.Receive(c, data, data.Length);
        }
        m.Advance();
        m.Outbox.Clear();
    }

    [Fact]
    public void Each_circle_lies_inside_the_one_before_and_on_the_map()
    {
        MapLayout map = CityMap.BuildV2();
        foreach (int seed in new[] { 1, 7, 99, 12345 })
        {
            Match m = Started(seed, map, out var clients);
            m.Players[0].Position = new Vector3(0, 0, 0);
            m.Players[1].Position = new Vector3(1, 0, 1);
            uint seq = 100;
            (Vector2 Center, float Radius) prevNext = m.NextZone;
            int stages = 0;
            Vector2 firstCenter = prevNext.Center;
            for (int t = 0; t < 30 * 600 && m.Phase == RWBR.Shared.Protocol.MatchPhase.Active; t += 30)
            {
                for (int k = 0; k < 30; k++)
                {
                    m.Players[0].Health = m.Players[1].Health = GameRules.MaxHealth; // stay alive, wherever the zone goes
                    Step(m, clients, ref seq);
                }
                var next = m.NextZone;
                Assert.True(Vector2.Distance(m.ZoneCenter, next.Center) + next.Radius <= m.ZoneRadius + 0.5f,
                    $"seed {seed}: next circle {next} leaves the current one ({m.ZoneCenter}, {m.ZoneRadius})");
                Assert.True(Math.Abs(next.Center.X) + next.Radius <= map.HalfSize + 0.5f || next.Radius > map.HalfSize);
                if (next.Center != prevNext.Center) stages++;
                prevNext = next;
            }
            Assert.True(stages >= 2, $"seed {seed}: the zone never moved");
        }
    }

    [Fact]
    public void Snapshots_carry_the_next_circle_and_seed_zero_stays_centred()
    {
        Match fixedZone = Started(0, CityMap.BuildV2(), out _);
        Assert.Equal(Vector2.Zero, fixedZone.NextZone.Center);
        Match moving = Started(42, CityMap.BuildV2(), out var clients);
        Assert.NotEqual(Vector2.Zero, moving.NextZone.Center);
        uint seq = 50;
        moving.Outbox.Clear();
        seq++;
        foreach (var c in clients)
        {
            byte[] data = RWBR.Shared.Protocol.PacketCodec.Encode(new List<InputCommand> { new() { Sequence = seq } });
            moving.Receive(c, data, data.Length);
        }
        moving.Advance();
        byte[] snap = moving.Outbox.First(o => o.To.Equals(clients[0])).Data;
        var s = RWBR.Shared.Protocol.SnapshotCodec.Decode(new RWBR.Shared.Protocol.PacketReader(snap, snap.Length), new RWBR.Shared.Protocol.SnapshotHistory());
        Assert.Equal(moving.NextZone.Center.X, s.NextZoneX, 3);
        Assert.Equal(moving.NextZone.Radius, s.NextZoneRadius, 3);
        Assert.True(s.NextZoneRadius < s.ZoneRadius);
    }

    [Fact]
    public void Zone_damage_grows_with_each_stage()
    {
        MapLayout map = MapLayout.GreyboxBlock(100f);
        var cfg = new MatchConfig { MinPlayers = 2, MaxPlayers = 2, Map = map, CountdownSeconds = 0.1f, ZoneDamagePerSecond = 10, ZoneDamageGrowth = 0.5f,
            ZoneStages = new List<ZoneStage> { new(1, 1, 50), new(1, 1, 20), new(100, 1, 0) } };
        var m = new Match(cfg);
        var clients = new List<System.Net.IPEndPoint> { new(System.Net.IPAddress.Loopback, 9310), new(System.Net.IPAddress.Loopback, 9311) };
        foreach (var c in clients)
        {
            byte[] hello = RWBR.Shared.Protocol.PacketCodec.Encode(new RWBR.Shared.Protocol.HelloPacket { Name = "z" });
            m.Receive(c, hello, hello.Length);
        }
        uint seq = 0;
        while (m.Phase != RWBR.Shared.Protocol.MatchPhase.Active) Step(m, clients, ref seq);
        for (int i = 0; i < 30 * 5; i++) Step(m, clients, ref seq); // into stage 3's hold
        m.Players[0].Position = new Vector3(90, 0, 90); // outside
        m.Players[0].Health = 100;
        for (int i = 0; i < 30; i++) Step(m, clients, ref seq);
        Assert.Equal(100 - 10 * 2f, m.Players[0].Health, 1); // stage 3 (index 2): 1 + 2 x 0.5 = 2x
    }
}

public class LootTableConfigTests
{
    [Fact]
    public void Welcome_carries_the_loot_table_and_clients_get_the_same_items_as_the_server()
    {
        var t = new LootTableConfig { Version = 7, FragPercent = 40, SmokePercent = 0, AttachmentPercent = 0, MediumRounds = 90 };
        byte[] bytes = RWBR.Shared.Protocol.PacketCodec.Encode(new RWBR.Shared.Protocol.WelcomePacket { MapVersion = CityMap.VersionV2, LootTable = t });
        var w = RWBR.Shared.Protocol.PacketCodec.DecodeWelcome(new RWBR.Shared.Protocol.PacketReader(bytes, bytes.Length));
        Assert.Equal(7, w.LootTable.Version);
        Assert.Equal(t.Key, w.LootTable.Key);

        MapLayout map = CityMap.BuildV2();
        var server = new Match(new MatchConfig { Map = map, LootTable = t });
        IReadOnlyList<LootSpawn> client = LootTable.Apply(map, w.LootTable);
        Assert.Equal(server.Loot.Select(l => (l.Kind, l.Count)), client.Select(l => (l.Kind, l.Count)));
        int frags = client.Count(l => l.Kind == ItemKind.Frag), defaultFrags = map.Loot.Count(l => l.Kind == ItemKind.Frag);
        Assert.True(frags > defaultFrags * 2, $"{frags} frags with 40 %, {defaultFrags} by default");
        Assert.Contains(client, l => l.Kind == ItemKind.AmmoMedium && l.Count == 90);
        Assert.Same(map.Loot, LootTable.Apply(map, LootTableConfig.Default)); // defaults: the map's own list
        var sameWeights = new LootTableConfig { Version = 9, FragPercent = 40, SmokePercent = 0, AttachmentPercent = 0, MediumRounds = 90 };
        Assert.Same(client, LootTable.Apply(map, sameWeights)); // cached by weights, whatever the version number
    }

    [Fact]
    public void Out_of_range_weights_are_clamped_not_rejected()
    {
        var c = new LootTableConfig { FragPercent = 90, SmokePercent = 50, AttachmentPercent = 50, LightRounds = 0, BandagesPerSpot = 99 }.Clamped();
        Assert.Equal(90, c.FragPercent);
        Assert.Equal(10, c.SmokePercent);
        Assert.Equal(0, c.AttachmentPercent);
        Assert.Equal(1, c.LightRounds);
        Assert.Equal(10, c.BandagesPerSpot);
    }

    [Fact]
    public void Assignments_from_the_platform_carry_the_loot_table()
    {
        string json = """{"matchId":"6f2c3c2e-8f4a-4f3e-9a54-1d1f0a1b2c3d","mapVersion":"hyd-city-v2","capacity":20,"minPlayers":2,"fillWaitSeconds":10,"disabledWeapons":[],"lootTable":{"version":3,"fragPercent":25}}""";
        var a = System.Text.Json.JsonSerializer.Deserialize<RWBR.MatchServer.Platform.MatchAssignment>(json, RWBR.MatchServer.Platform.PlatformClient.Json)!;
        Assert.Equal(3, a.LootTable!.Version);
        Assert.Equal(25, a.LootTable.FragPercent);
        Assert.Equal(5, a.LootTable.SmokePercent); // not sent: the default
    }
}
