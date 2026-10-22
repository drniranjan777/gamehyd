using System.Linq;
using System.Numerics;
using RWBR.MatchServer.Sim;
using RWBR.Shared;
using RWBR.Shared.Bots;
using RWBR.Shared.Protocol;
using Xunit;

namespace RWBR.MatchServer.Tests;

/// <summary>Phase 8: bots navigate the city on the ground and loot, so the simulator can evaluate a loot-first start.</summary>
public class BotNavigationTests
{
    [Fact]
    public void Paths_on_the_city_avoid_buildings_and_reach_the_goal()
    {
        MapLayout city = CityMap.Build();
        NavGrid nav = NavGrid.For(city);
        Assert.Same(nav, NavGrid.For(city)); // cached per map

        int walkable = 0;
        foreach (Vector3 spawn in city.Spawns)
        {
            Assert.True(nav.IsWalkable(spawn), $"spawn {spawn} should be on walkable ground");
            var path = nav.FindPath(spawn, Vector3.Zero);
            Assert.NotNull(path);
            Vector3 prev = spawn;
            foreach (Vector3 wp in path!)
            {
                Assert.True(nav.ClearLine(prev, wp), $"segment {prev} -> {wp} crosses a wall");
                prev = wp;
            }
            Assert.True(Vector2.Distance(new Vector2(prev.X, prev.Z), Vector2.Zero) < NavGrid.CellSize * 3);
            walkable++;
        }
        Assert.Equal(city.Spawns.Count, walkable);
    }

    [Fact]
    public void Most_ground_level_weapon_loot_is_reachable_from_the_centre()
    {
        MapLayout city = CityMap.Build();
        NavGrid nav = NavGrid.For(city);
        var ground = city.Loot.Where(l => l.WeaponIndex >= 0 && l.Position.Y < 1f).ToList();
        int reachable = ground.Count(l => nav.FindPath(Vector3.Zero, l.Position) != null);
        Assert.True(ground.Count > 50, "the city should place plenty of ground-level weapons");
        Assert.True(reachable >= ground.Count * 0.8, $"only {reachable} of {ground.Count} ground weapons are reachable");
    }

    [Fact]
    public void Unarmed_bots_loot_weapons_and_finish_a_city_match()
    {
        var cfg = new MatchConfig
        {
            MinPlayers = 12,
            MaxPlayers = 12,
            Map = CityMap.Build(),
            CountdownSeconds = 3,
            StartWithWeapon = false,
        };
        cfg.ZoneStages = MatchConfig.ZonesFor(cfg.Map);
        SimulatedMatch r = MatchSimulator.Run(cfg, seed: 7);
        Assert.Equal(CompletionReason.Winner, r.Reason);
        Assert.True(r.ShotsFired > 0, "bots should have found weapons and fired");
        Assert.True(r.PlayerKills > 0, "someone should have been eliminated by another player");
    }
}
