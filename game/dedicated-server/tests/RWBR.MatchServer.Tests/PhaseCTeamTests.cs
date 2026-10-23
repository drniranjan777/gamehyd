using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Bots;
using RWBR.Shared.Protocol;
using RWBR.Shared.Server;
using Xunit;

namespace RWBR.MatchServer.Tests;

/// <summary>Phase C part 6a: Duo and Squad in the shared match.</summary>
public class TeamTests
{
    private static uint _seq = 20000;
    private static readonly MapLayout Open = new("open", 120, Array.Empty<Aabb>());

    private static Match Started(int players, int teamSize, out List<IPEndPoint> clients, MapLayout? map = null, float zoneRadius = 0)
    {
        var cfg = new MatchConfig
        {
            Map = map ?? Open, MinPlayers = players, MaxPlayers = players, TeamSize = teamSize, CountdownSeconds = 0.1f,
            StartWithWeapon = true, MaxMatchSeconds = 1000, Vehicles = false, ZoneDamagePerSecond = 30,
            ZoneStages = zoneRadius > 0
                ? new List<ZoneStage> { new(0, 0.05f, zoneRadius), new(900, 1, zoneRadius) }
                : new List<ZoneStage> { new(900, 1, 50) },
        };
        var m = new Match(cfg);
        clients = Enumerable.Range(0, players).Select(i => new IPEndPoint(IPAddress.Loopback, 9900 + i)).ToList();
        foreach (IPEndPoint c in clients)
        {
            byte[] hello = PacketCodec.Encode(new HelloPacket { Name = "t" });
            m.Receive(c, hello, hello.Length);
        }
        while (m.Phase != MatchPhase.Active) Tick(m, clients);
        return m;
    }

    private static void Tick(Match m, List<IPEndPoint> clients, Dictionary<int, InputCommand>? cmds = null)
    {
        _seq++;
        for (int i = 0; i < clients.Count; i++)
        {
            InputCommand c = cmds != null && cmds.TryGetValue(i, out InputCommand x) ? x : new InputCommand();
            c.Sequence = _seq;
            byte[] data = PacketCodec.Encode(new List<InputCommand> { c });
            m.Receive(clients[i], data, data.Length);
        }
        m.Advance();
        m.Outbox.Clear();
    }

    [Fact]
    public void Players_join_teams_in_order_and_teammates_start_together()
    {
        Match m = Started(4, 2, out _);
        Assert.Equal(new[] { 0, 0, 1, 1 }, m.Players.Select(p => p.Team));
        Assert.True(Vector3.Distance(m.Players[0].Position, m.Players[1].Position) < 4f, "teammates start side by side");
        Assert.True(Vector3.Distance(m.Players[0].Position, m.Players[2].Position) > 10f, "other teams start elsewhere");
    }

    [Fact]
    public void Shots_pass_through_teammates_but_hit_the_enemy_behind()
    {
        Match m = Started(4, 2, out var clients);
        PlayerState shooter = m.Players[0], mate = m.Players[1], enemy = m.Players[2];
        shooter.Position = new Vector3(0, 0, 0);
        mate.Position = new Vector3(0, 0, 5);
        enemy.Position = new Vector3(0, 0, 10);
        m.Players[3].Position = new Vector3(80, 0, 80);
        for (int i = 0; i < 10; i++) Tick(m, clients, new() { [0] = new InputCommand { Buttons = InputButtons.Fire } });
        Assert.Equal(GameRules.MaxHealth, mate.Health);
        Assert.True(enemy.Health < GameRules.MaxHealth || !enemy.Alive);
    }

    [Fact]
    public void A_team_places_when_its_last_member_is_out_and_the_last_team_standing_wins_together()
    {
        // A small zone from the start: anyone moved far outside with almost no health is eliminated within a tick.
        Match m = Started(6, 2, out var clients, zoneRadius: 20); // teams 0, 1, 2
        foreach (PlayerState p in m.Players) p.Position = Vector3.Zero;
        void PushOut(PlayerState p)
        {
            p.Position = new Vector3(100, 0, 100);
            p.Health = 0.1f;
            for (int i = 0; i < 5 && p.Alive; i++) Tick(m, clients);
            Assert.False(p.Alive);
        }

        PlayerState a1 = m.Players[2], a2 = m.Players[3]; // team 1
        PushOut(a1);
        Assert.Equal(0, a1.Placement); // their teammate is still in
        PushOut(a2);
        Assert.Equal(3, a1.Placement); // team 1 was the first of three teams out
        Assert.Equal(3, a2.Placement);

        PushOut(m.Players[4]);
        Assert.Equal(MatchPhase.Active, m.Phase);
        m.Players[5].Position = new Vector3(100, 0, 100);
        m.Players[5].Health = 0.1f;
        for (int i = 0; i < 5 && m.Phase == MatchPhase.Active; i++) Tick(m, clients);
        Assert.Equal(2, m.Players[4].Placement);
        Assert.Equal(2, m.Players[5].Placement);
        Assert.Equal(1, m.Players[0].Placement);
        Assert.Equal(1, m.Players[1].Placement);
        Assert.Equal(CompletionReason.Winner, m.Result!.Reason);
        Assert.Contains(m.Result.WinnerId, new[] { m.Players[0].Id, m.Players[1].Id });
    }

    [Fact]
    public void Frags_and_cars_spare_the_team()
    {
        Match m = Started(4, 2, out var clients, CityMap.Build());
        PlayerState thrower = m.Players[0], mate = m.Players[1], enemy = m.Players[2];
        thrower.Position = new Vector3(0, 0, -200);
        mate.Position = new Vector3(0, 0, -190);
        enemy.Position = new Vector3(1.5f, 0, -190);
        m.Players[3].Position = new Vector3(-200, 0, -200);
        thrower.Combat.SetCount(ItemKind.Frag, 1);
        Tick(m, clients, new() { [0] = new InputCommand { Buttons = InputButtons.Throw, Item = ItemKind.Frag, Pitch = -0.1f } });
        for (int i = 0; i < 30 * 4; i++) Tick(m, clients);
        Assert.Equal(GameRules.MaxHealth, mate.Health);
        Assert.True(enemy.Health < GameRules.MaxHealth || !enemy.Alive, "the enemy next to them is hit");
    }

    [Fact]
    public void Fill_bots_complete_a_lone_players_team_and_snapshots_name_the_teammates()
    {
        var cfg = new MatchConfig
        {
            Map = Open, MinPlayers = 1, MaxPlayers = 8, FillBotsTo = 8, TeamSize = 4, CountdownSeconds = 0.5f, Vehicles = false,
            ZoneStages = new List<ZoneStage> { new(900, 1, 50) }, MaxMatchSeconds = 1000,
        };
        var m = new Match(cfg);
        var me = new IPEndPoint(IPAddress.Loopback, 9950);
        byte[] hello = PacketCodec.Encode(new HelloPacket { Name = "me" });
        m.Receive(me, hello, hello.Length);
        WelcomePacket? welcome = null;
        SnapshotPacket? snap = null;
        var history = new SnapshotHistory();
        for (int i = 0; i < 60; i++)
        {
            byte[] input = PacketCodec.Encode(new List<InputCommand> { new() { Sequence = (uint)(i + 1) } });
            m.Receive(me, input, input.Length);
            m.Advance();
            foreach (var (to, data) in m.Outbox.Where(o => o.To.Equals(me)))
            {
                var r = new PacketReader(data, data.Length);
                if (r.Type == PacketType.Welcome) welcome = PacketCodec.DecodeWelcome(r);
                else if (r.Type == PacketType.Snapshot)
                {
                    snap = SnapshotCodec.Decode(r, history);
                    history.Add(snap);
                }
            }
            m.Outbox.Clear();
        }
        Assert.Equal(4, welcome!.TeamSize);
        PlayerState human = m.Players.Single(p => !p.IsBot);
        Assert.Equal(3, m.Players.Count(p => p.IsBot && p.Team == human.Team));
        Assert.Equal(3, snap!.SelfTeammates.Count);
        Assert.All(snap.SelfTeammates, id => Assert.True(m.Players.Single(p => p.Id == id).IsBot));
    }

    [Fact]
    public void Bots_never_aim_at_teammates()
    {
        var bot = new BotBrain(1, Open, BotSkill.For(BotDifficulty.Hard));
        var s = new SnapshotPacket { ZoneRadius = 500, NextZoneRadius = 500, SelfReserveAmmo = 60 };
        s.SelfSlotWeapon[0] = WeaponCatalog.Rifle;
        s.SelfSlotAmmo[0] = 20;
        s.Players.Add(new SnapshotEntry { PlayerId = 1, Health = 100, Ammo = 20, Weapon = WeaponCatalog.Rifle, Flags = PlayerFlags.Alive | PlayerFlags.HasWeapon | PlayerFlags.Grounded });
        s.Players.Add(new SnapshotEntry { PlayerId = 2, Z = 10, Health = 100, Flags = PlayerFlags.Alive | PlayerFlags.Grounded });
        s.SelfTeammates.Add(2);
        for (double t = 0; t < 2; t += 0.1) Assert.False(bot.Decide(s, 1, t).Has(InputButtons.Fire));
    }

    [Fact]
    public void A_duo_bot_match_runs_to_a_team_result()
    {
        MatchConfig cfg = LocalMatch.BotMatchConfig(MapLayout.GreyboxBlock(100f), 7);
        cfg.TeamSize = 2;
        cfg.ZoneSeed = 5;
        var local = new LocalMatch(cfg, 7, seed: 2);
        var me = new IPEndPoint(IPAddress.Loopback, 1);
        local.SendFromPlayer(PacketCodec.Encode(new HelloPacket { Name = "me" }));
        uint seq = 0;
        while (!local.Match.IsFinished && local.Match.Tick < 30 * 900)
        {
            local.SendFromPlayer(PacketCodec.Encode(new List<InputCommand> { new() { Sequence = ++seq } }));
            local.Tick();
            while (local.TryReceiveForPlayer(out _)) { }
        }
        MatchCompletedPacket r = local.Match.Result!;
        Assert.NotNull(r);
        var byTeam = local.Match.Players.GroupBy(p => p.Team).ToList();
        Assert.Equal(4, byTeam.Count);
        Assert.All(byTeam, g => Assert.Single(g.Select(p => p.Placement).Distinct())); // teammates share their placement
        Assert.Equal(byTeam.Count, byTeam.Select(g => g.First().Placement).Distinct().Count());
    }
}
