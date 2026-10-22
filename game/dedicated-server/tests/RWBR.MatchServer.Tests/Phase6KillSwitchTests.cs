using System;
using System.Linq;
using System.Text.Json;
using RWBR.MatchServer.Platform;
using RWBR.Shared;
using Xunit;
using Match = RWBR.MatchServer.Match;

namespace RWBR.MatchServer.Tests;

/// <summary>Live-ops kill switch: weapons the platform disables never spawn and are never the starting weapon.</summary>
public class KillSwitchTests
{
    [Fact]
    public void Disabling_the_starting_weapon_picks_the_next_one_and_reports_unknown_ids()
    {
        var cfg = new MatchConfig();
        var unknown = cfg.DisableWeapons(new[] { "rifle_standard", "kestrel_smg", "laser_cannon" });
        Assert.Equal(new[] { "laser_cannon" }, unknown);
        Assert.True(cfg.StartWithWeapon);
        Assert.Equal(WeaponCatalog.Pistol, cfg.StartingWeaponIndex);
        Assert.Same(WeaponCatalog.Get(WeaponCatalog.Pistol), cfg.Weapon);
    }

    [Fact]
    public void Disabling_every_weapon_starts_players_unarmed()
    {
        var cfg = new MatchConfig();
        cfg.DisableWeapons(WeaponCatalog.All.Select(w => w.Id));
        Assert.False(cfg.StartWithWeapon);
    }

    [Fact]
    public void Disabled_weapon_loot_never_spawns_on_the_city()
    {
        var cfg = new MatchConfig { Map = CityMap.Build() };
        cfg.DisableWeapons(new[] { "longshot_sr" });
        var match = new Match(cfg);
        var loot = cfg.Map.Loot;
        int snipers = 0;
        for (int i = 0; i < loot.Count; i++)
        {
            bool sniper = loot[i].WeaponIndex == WeaponCatalog.Sniper;
            if (sniper) snipers++;
            Assert.Equal(!sniper, match.LootAvailable(i));
        }
        Assert.True(snipers > 0, "the city should place some sniper loot");
    }

    [Fact]
    public void Heartbeat_assignments_carry_the_disabled_weapons()
    {
        const string json = "{\"serverId\":\"5b0f7a3e-0000-4000-8000-000000000001\",\"assignment\":{\"matchId\":\"5b0f7a3e-0000-4000-8000-000000000002\"," +
                            "\"mapVersion\":\"hyd-city-v1\",\"capacity\":30,\"minPlayers\":2,\"fillWaitSeconds\":20,\"disabledWeapons\":[\"longshot_sr\"]}}";
        var hb = JsonSerializer.Deserialize<HeartbeatResponse>(json, PlatformClient.Json)!;
        Assert.Equal(new[] { "longshot_sr" }, hb.Assignment!.DisabledWeapons);
        var old = JsonSerializer.Deserialize<HeartbeatResponse>(json.Replace(",\"disabledWeapons\":[\"longshot_sr\"]", ""), PlatformClient.Json)!;
        Assert.Null(old.Assignment!.DisabledWeapons);
    }
}

/// <summary>Phase 7: live tick health in heartbeats.</summary>
public class TickHealthTests
{
    [Fact]
    public void Heartbeats_carry_the_recent_tick_p99_as_camel_case_json()
    {
        var hb = new HeartbeatRequest("127.0.0.1:7777", "IN-SOUTH", "t", new[] { "hyd-city-v1" }, "HOSTING", Guid.NewGuid(), "ACTIVE", 12, 4.25);
        string json = JsonSerializer.Serialize(hb, PlatformClient.Json);
        Assert.Contains("\"tickMsP99\":4.25", json);
        Assert.Contains("\"connectedPlayers\":12", json);
    }

    [Fact]
    public async System.Threading.Tasks.Task Host_reports_tick_p99_while_running()
    {
        var cfg = MatchConfig.Fast(2);
        var match = new Match(cfg);
        using var host = new UdpMatchHost(match, 0);
        Assert.Null(host.RecentTickMsP99());
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(1));
        var run = host.RunAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(400);
        double? p99 = host.RecentTickMsP99();
        Assert.NotNull(p99);
        Assert.InRange(p99!.Value, 0, 50);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }
}
