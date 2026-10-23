using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using RWBR.Shared.Server;
using Xunit;

namespace RWBR.MatchServer.Tests;

/// <summary>Phase C part 6b: the plane and parachute drop.</summary>
public class DropTests
{
    private const float Dt = 1f / 30;
    private static readonly MapLayout Open = new("open", 300, Array.Empty<Aabb>());

    private static MotorState Falling(float height) =>
        new() { Position = new Vector3(0, height, 0), Drop = DropPhase.Freefall };

    [Fact]
    public void Free_fall_opens_the_parachute_near_the_ground_and_lands_ready_to_walk()
    {
        MotorState m = Falling(GameRules.PlaneAltitude);
        bool opened = false;
        int ticks = 0;
        while (m.Dropping && ticks++ < 30 * 60)
        {
            Simulation.Step(ref m, new InputCommand(), Dt, Open);
            if (m.Drop == DropPhase.Parachute && !opened)
            {
                opened = true;
                Assert.InRange(m.Position.Y, GameRules.ParachuteAutoHeight - 2, GameRules.ParachuteAutoHeight + 0.1f);
            }
        }
        Assert.True(opened);
        Assert.Equal(DropPhase.None, m.Drop);
        Assert.True(m.Grounded);
        Assert.Equal(0f, m.Position.Y);
        float expected = (GameRules.PlaneAltitude - GameRules.ParachuteAutoHeight) / GameRules.FreefallFallSpeed +
                         GameRules.ParachuteAutoHeight / GameRules.ParachuteFallSpeed;
        Assert.InRange(ticks * Dt, expected - 0.5f, expected + 0.5f);
    }

    [Fact]
    public void Jump_opens_the_parachute_early_only_when_high_enough_and_free_fall_steers_faster()
    {
        MotorState high = Falling(100), low = Falling(GameRules.ParachuteMinHeight - 5);
        Simulation.Step(ref high, new InputCommand { Buttons = InputButtons.Jump }, Dt, Open);
        Simulation.Step(ref low, new InputCommand { Buttons = InputButtons.Jump }, Dt, Open);
        Assert.Equal(DropPhase.Parachute, high.Drop);
        Assert.Equal(DropPhase.Freefall, low.Drop);

        MotorState fall = Falling(150), chute = Falling(150);
        chute.Drop = DropPhase.Parachute;
        for (int i = 0; i < 30; i++)
        {
            Simulation.Step(ref fall, new InputCommand { MoveZ = 1 }, Dt, Open);
            Simulation.Step(ref chute, new InputCommand { MoveZ = 1 }, Dt, Open);
        }
        Assert.Equal(GameRules.FreefallSpeed, fall.Position.Z, 1);
        Assert.Equal(GameRules.ParachuteSpeed, chute.Position.Z, 1);
    }

    [Fact]
    public void Landing_in_the_lake_puts_you_on_the_nearest_dry_ground()
    {
        MapLayout city = CityMap.Build();
        var m = new MotorState { Position = new Vector3(170, 3, -170), Drop = DropPhase.Parachute }; // over the lake's middle
        for (int i = 0; i < 30 * 5 && m.Dropping; i++) Simulation.Step(ref m, new InputCommand(), Dt, city);
        Assert.Equal(DropPhase.None, m.Drop);
        Assert.Equal(m.Position, Simulation.ResolveCollisions(m.Position, city)); // standing somewhere legal, not in water
    }

    private static uint _seq = 30000;

    private static Match Started(int players, out List<IPEndPoint> clients)
    {
        var cfg = new MatchConfig
        {
            Map = CityMap.Build(), MinPlayers = players, MaxPlayers = players, CountdownSeconds = 0.1f, PlaneDrop = true,
            ZoneSeed = 9, StartWithWeapon = true, ZoneStages = MatchConfig.ZonesFor(CityMap.Build()), MaxMatchSeconds = 1000,
        };
        var m = new Match(cfg);
        clients = Enumerable.Range(0, players).Select(i => new IPEndPoint(IPAddress.Loopback, 9960 + i)).ToList();
        foreach (IPEndPoint c in clients)
        {
            byte[] hello = PacketCodec.Encode(new HelloPacket { Name = "d" });
            m.Receive(c, hello, hello.Length);
        }
        while (m.Phase != MatchPhase.Active) Tick(m, clients);
        return m;
    }

    private static void Tick(Match m, List<IPEndPoint> clients, InputCommand first = default)
    {
        _seq++;
        for (int i = 0; i < clients.Count; i++)
        {
            InputCommand c = i == 0 ? first : new InputCommand();
            c.Sequence = _seq;
            byte[] d = PacketCodec.Encode(new List<InputCommand> { c });
            m.Receive(clients[i], d, d.Length);
        }
        m.Advance();
    }

    [Fact]
    public void Everyone_starts_in_the_plane_jumps_or_is_dropped_and_lands()
    {
        Match m = Started(3, out var clients);
        m.Outbox.Clear();
        Assert.All(m.Players, p => Assert.Equal(DropPhase.Plane, p.Motor.Drop));
        Assert.True(m.PlaneFlying);
        Vector3 start = m.PlanePosition;
        Assert.Equal(GameRules.PlaneAltitude, start.Y);
        Assert.True(Math.Abs(start.X) <= CityMap.HalfSize && Math.Abs(start.Z) <= CityMap.HalfSize, "the line starts over the map");

        for (int i = 0; i < 60; i++) Tick(m, clients);
        Assert.True(Vector3.Distance(start, m.PlanePosition) > 100, "the plane flies on");
        Assert.All(m.Players, p => Assert.Equal(m.PlanePosition.X, p.Position.X, 1)); // everyone aboard rides along

        // Player 0 jumps and tries to shoot on the way down: no shots while dropping.
        Tick(m, clients, new InputCommand { Buttons = InputButtons.Jump });
        Assert.Equal(DropPhase.Freefall, m.Players[0].Motor.Drop);
        long shots = m.Stats.ShotsFired;
        for (int i = 0; i < 30; i++) Tick(m, clients, new InputCommand { Buttons = InputButtons.Fire });
        Assert.Equal(shots, m.Stats.ShotsFired);

        // Snapshots tell everyone who is falling and where the plane is.
        m.Outbox.Clear();
        Tick(m, clients);
        byte[] snap = m.Outbox.Last(o => o.To.Equals(clients[1])).Data;
        SnapshotPacket s = SnapshotCodec.Decode(new PacketReader(snap, snap.Length), new SnapshotHistory());
        Assert.True(s.PlaneFlying);
        Assert.True((s.Players.Single(e => e.PlayerId == m.Players[0].Id).Flags & PlayerFlags.Freefall) != 0);
        Assert.True((s.Players.Single(e => e.PlayerId == m.Players[1].Id).Flags & PlayerFlags.InPlane) != 0);

        // Those who never jump are dropped at the end of the line; everyone lands.
        for (int i = 0; i < 30 * 40; i++) Tick(m, clients);
        Assert.False(m.PlaneFlying);
        Assert.All(m.Players, p => Assert.Equal(DropPhase.None, p.Motor.Drop));
        Assert.All(m.Players, p => Assert.True(p.Motor.Grounded));
    }

    [Fact]
    public void Bots_jump_land_on_the_ground_and_spread_from_the_plane()
    {
        MatchConfig cfg = LocalMatch.BotMatchConfig(CityMap.BuildV2(), 9);
        Assert.True(cfg.PlaneDrop);
        cfg.ZoneSeed = 4;
        var local = new LocalMatch(cfg, 9, seed: 6);
        local.SendFromPlayer(PacketCodec.Encode(new HelloPacket { Name = "me" }));
        uint seq = 0;
        for (int i = 0; i < 30 * 60; i++)
        {
            local.SendFromPlayer(PacketCodec.Encode(new List<InputCommand> { new() { Sequence = ++seq } }));
            local.Tick();
            while (local.TryReceiveForPlayer(out _)) { }
        }
        var bots = local.Match.Players.Where(p => local.Bots.Any(b => b.PlayerId == p.Id)).ToList();
        Assert.All(bots, b => Assert.Equal(DropPhase.None, b.Motor.Drop));
        // On the ground (or a few steps up some stairs since landing), never stranded on a roof.
        Assert.All(bots, b => Assert.True(b.Position.Y < MapLayout.RoofTop - 0.5f || !b.Alive, $"bot {b.Id} is up on a roof at {b.Position}"));
        Assert.True(bots.Select(b => (int)(b.Position.X / 50)).Distinct().Count() > 1, "not all in one spot");
    }
}
