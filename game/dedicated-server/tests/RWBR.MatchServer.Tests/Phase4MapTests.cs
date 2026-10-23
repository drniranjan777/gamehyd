using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RWBR.Bots;
using RWBR.Shared;
using RWBR.Shared.Server;
using RWBR.Shared.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace RWBR.MatchServer.Tests;

public class CityMapTests
{
    private readonly ITestOutputHelper _out;
    private static readonly MapLayout City = CityMap.Build();

    public CityMapTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// hyd-city-v1 is published: its geometry and loot must never change. If this fails, the generator changed;
    /// publish the change as a new map version (hyd-city-v2) instead of editing v1.
    /// </summary>
    [Fact]
    public void Published_city_geometry_is_pinned()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < City.Obstacles.Count; i++)
        {
            Aabb b = City.Obstacles[i];
            sb.Append(FormattableString.Invariant($"{b.Min.X:F3},{b.Min.Y:F3},{b.Min.Z:F3},{b.Max.X:F3},{b.Max.Y:F3},{b.Max.Z:F3},{(int)City.KindOf(i)};"));
        }
        foreach (LootSpawn l in City.Loot) sb.Append(FormattableString.Invariant($"{l.Type},{l.Position.X:F3},{l.Position.Y:F3},{l.Position.Z:F3},{l.Quantity};"));
        foreach (Vector3 s in City.Spawns) sb.Append(FormattableString.Invariant($"{s.X:F1},{s.Z:F1};"));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        _out.WriteLine($"{City.Obstacles.Count} boxes, {City.Loot.Count} loot, {City.Spawns.Count} spawns, sha256 {hash}");
        Assert.Equal(PinnedHash, hash);
    }

    private const string PinnedHash = "767DC0BCF7944070946E4FDC2A6D7C7CFBABCB7A5CC818EC32295409EF7089D3";

    [Fact]
    public void City_has_all_districts_and_content()
    {
        Assert.Equal(CityMap.Version, City.Version);
        Assert.Equal(5, City.Districts.Count);
        Assert.True(City.Obstacles.Count > 1000, $"only {City.Obstacles.Count} boxes");
        Assert.True(City.Loot.Count > 150, $"only {City.Loot.Count} loot spawns");
        Assert.True(City.Spawns.Count >= 30);
        foreach (int w in Enumerable.Range(0, WeaponCatalog.Count))
            Assert.Contains(City.Loot, l => l.WeaponIndex == w); // every weapon can be found
        Assert.Equal("Old City Bazaar", City.DistrictAt(-150, 150));
        Assert.Equal("Char Gate Plaza", City.DistrictAt(0, 0));
        Assert.True(MapLayout.TryFromVersion("hyd-city-v1", out MapLayout same) && ReferenceEquals(same, City));
    }

    [Fact]
    public void Spawns_are_clear_and_loot_is_not_buried_in_walls()
    {
        foreach (Vector3 s in City.Spawns)
        {
            Assert.Equal(s, Simulation.ResolveCollisions(s, City));
            Assert.Equal(0f, Simulation.GroundHeight(s, s.Y + GameRules.StepHeight, City));
        }
        var buried = City.Loot.Where(l => Simulation.ResolveHorizontal(l.Position, l.Position.Y, 0.5f, City) != l.Position &&
                                          !Simulation.HasHeadroom(l.Position, 0.5f, City)).ToList();
        foreach (LootSpawn l in buried)
        {
            var near = new List<int>();
            City.Grid.Query(l.Position.X - 1, l.Position.Z - 1, l.Position.X + 1, l.Position.Z + 1, near);
            _out.WriteLine($"buried {l.Type} at {l.Position} in {City.DistrictAt(l.Position.X, l.Position.Z)}: " +
                           string.Join(" | ", near.Select(i => $"{City.KindOf(i)} {City.Obstacles[i].Min}-{City.Obstacles[i].Max}")));
        }
        Assert.Empty(buried);
    }

    [Fact]
    public void Grid_queries_match_a_brute_force_scan()
    {
        var rng = new Random(42);
        for (int i = 0; i < 2000; i++)
        {
            var o = new Vector3((float)(rng.NextDouble() * 600 - 300), (float)(rng.NextDouble() * 20), (float)(rng.NextDouble() * 600 - 300));
            Vector3 d = Vector3.Normalize(new Vector3((float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5) * 0.3f, (float)(rng.NextDouble() - 0.5)));
            float range = (float)(rng.NextDouble() * 300);
            Assert.Equal(Simulation.WallDistanceBruteForce(o, d, range, City), Simulation.WallDistance(o, d, range, City), 4);
        }

        var found = new List<int>();
        for (int i = 0; i < 500; i++)
        {
            float x = (float)(rng.NextDouble() * 600 - 300), z = (float)(rng.NextDouble() * 600 - 300), r = (float)(rng.NextDouble() * 10);
            City.Grid.Query(x - r, z - r, x + r, z + r, found);
            var set = new HashSet<int>(found);
            Assert.Equal(found.Count, set.Count); // no duplicates
            for (int b = 0; b < City.Obstacles.Count; b++)
            {
                Aabb box = City.Obstacles[b];
                bool overlaps = box.Min.X <= x + r && box.Max.X >= x - r && box.Min.Z <= z + r && box.Max.Z >= z - r;
                if (overlaps) Assert.Contains(b, set);
            }
        }
    }

    [Fact]
    public void A_player_enters_a_house_and_climbs_the_inside_stairs_to_the_roof()
    {
        MapLayout kit = CityMap.KitSample();
        const float dt = 1 / 30f, north = 0, south = MathF.PI, east = MathF.PI / 2;
        MotorState s = MotorState.StandingAt(new Vector3(0, 0, 9)); // outside, north of the door

        void Walk(float yaw, Func<MotorState, bool> until, int maxTicks = 300)
        {
            for (int i = 0; i < maxTicks && !until(s); i++) Simulation.Step(ref s, new InputCommand { MoveZ = 1, Yaw = yaw }, dt, kit);
        }

        Walk(south, m => m.Position.Z < -4.4f);          // through the north door, across the room
        Assert.True(s.Position.Z < -4.4f, $"stuck at {s.Position}");
        Walk(east, m => m.Position.X > 5.3f);             // onto the first step
        Assert.True(s.Position.X > 5.3f, $"could not reach the stairs: {s.Position}");
        Walk(north, m => m.Position.Y >= MapLayout.RoofTop - 0.01f && m.Position.Z > 4f);
        Assert.Equal(MapLayout.RoofTop, s.Position.Y, 2);
        Walk(-MathF.PI / 2, m => m.Position.X < 0);       // west onto the roof
        Assert.Equal(MapLayout.RoofTop, s.Position.Y, 2);
        Assert.True(s.Grounded);
    }

    [Fact]
    public void Movement_and_line_of_sight_stay_cheap_on_the_city()
    {
        var motors = City.Spawns.Take(30).Select(MotorState.StandingAt).ToArray();
        var sw = Stopwatch.StartNew();
        const int ticks = 900; // 30 s of a 30-player match
        for (int t = 0; t < ticks; t++)
            for (int p = 0; p < motors.Length; p++)
                Simulation.Step(ref motors[p], new InputCommand { MoveZ = 1, Yaw = p, Buttons = InputButtons.Sprint }, 1 / 30f, City);
        double stepUs = sw.Elapsed.TotalMilliseconds * 1000 / (ticks * motors.Length);

        sw.Restart();
        var rng = new Random(1);
        for (int i = 0; i < 20_000; i++)
            Simulation.WallDistance(new Vector3(rng.Next(-290, 290), 1.6f, rng.Next(-290, 290)), Vector3.Normalize(new Vector3(rng.Next(-9, 10) + 0.1f, 0, rng.Next(-9, 10))), 120, City);
        double rayUs = sw.Elapsed.TotalMilliseconds * 1000 / 20_000;
        _out.WriteLine($"step {stepUs:0.0} µs, ray {rayUs:0.0} µs");
        Assert.True(stepUs < 50, $"Step {stepUs} µs");
        Assert.True(rayUs < 100, $"WallDistance {rayUs} µs");
    }

    [Fact]
    public async Task Bots_play_a_match_on_the_city()
    {
        MatchConfig cfg = MatchConfig.Fast(8);
        cfg.Map = City;
        cfg.ZoneStages = new List<ZoneStage> { new ZoneStage(2, 8, 60), new ZoneStage(1, 6, 0) };
        cfg.ZoneDamagePerSecond = 40;
        var match = new Match(cfg);
        using var host = new UdpMatchHost(match, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Task<HostReport> server = host.RunAsync(cts.Token);
        var bots = await Task.Run(() => BotSwarm.Run(new IPEndPoint(IPAddress.Loopback, host.Port), 8, TimeSpan.FromSeconds(80), ct: cts.Token));
        HostReport report = await server;
        _out.WriteLine($"active {report.ActiveSeconds}s, tick p99 {report.TickMsP99} ms");
        Assert.Equal(CompletionReason.Winner, report.Reason);
        Assert.All(bots, b => Assert.NotNull(b.Result));
        foreach (BotClient b in bots) b.Dispose();
    }
}
