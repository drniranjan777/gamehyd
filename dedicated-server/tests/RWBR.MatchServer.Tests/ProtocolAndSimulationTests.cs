using System;
using System.Collections.Generic;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using Xunit;

namespace RWBR.MatchServer.Tests;

public class ProtocolTests
{
    [Fact]
    public void Snapshot_round_trips()
    {
        var snap = new SnapshotPacket { Tick = 42, Phase = MatchPhase.Active, ZoneRadius = 12.5f, AckSequence = 7, AliveCount = 2 };
        snap.Players.Add(new SnapshotEntry { PlayerId = 3, X = 1.5f, Z = -2f, Yaw = 0.25f, Health = 80, Ammo = 12, Flags = PlayerFlags.Alive | PlayerFlags.Connected });
        byte[] bytes = PacketCodec.Encode(snap);

        var r = new PacketReader(bytes, bytes.Length);
        Assert.Equal(PacketType.Snapshot, r.Type);
        SnapshotPacket back = PacketCodec.DecodeSnapshot(r);
        Assert.Equal(42u, back.Tick);
        Assert.Equal(12.5f, back.ZoneRadius);
        Assert.Equal(7u, back.AckSequence);
        SnapshotEntry e = Assert.Single(back.Players);
        Assert.Equal((ushort)3, e.PlayerId);
        Assert.Equal(-2f, e.Z);
        Assert.Equal((byte)80, e.Health);
    }

    [Fact]
    public void Input_round_trips_newest_first()
    {
        var inputs = new List<InputCommand>
        {
            new() { Sequence = 9, AckTick = 100, MoveZ = 1, Yaw = 1.5f, Buttons = InputButtons.Fire },
            new() { Sequence = 8, AckTick = 99 },
        };
        byte[] bytes = PacketCodec.Encode(inputs);
        List<InputCommand> back = PacketCodec.DecodeInput(new PacketReader(bytes, bytes.Length));
        Assert.Equal(new uint[] { 9, 8 }, back.ConvertAll(i => i.Sequence));
        Assert.True(back[0].Has(InputButtons.Fire));
    }

    [Fact]
    public void Truncated_or_oversized_packets_are_rejected()
    {
        byte[] bytes = PacketCodec.Encode(new HelloPacket { Name = "player" });
        Assert.Throws<PacketFormatException>(() => PacketCodec.DecodeHello(new PacketReader(bytes, bytes.Length - 3)));

        byte[] tooMany = { (byte)PacketType.Input, 50 };
        Assert.Throws<PacketFormatException>(() => PacketCodec.DecodeInput(new PacketReader(tooMany, tooMany.Length)));
        Assert.Throws<PacketFormatException>(() => new PacketReader(Array.Empty<byte>(), 0));
    }
}

public class SimulationTests
{
    private static readonly MapLayout Open = new("open", 100, Array.Empty<Aabb>());

    [Fact]
    public void Oversized_move_input_cannot_exceed_sprint_speed()
    {
        var hack = new InputCommand { MoveZ = 1000, MoveX = 1000, Buttons = InputButtons.Sprint };
        Vector3 p = Simulation.Move(Vector3.Zero, hack, 1f, Open);
        Assert.True(p.Length() <= GameRules.SprintSpeed + 1e-3f, $"moved {p.Length()} m in 1 s");
    }

    [Fact]
    public void Non_finite_input_does_not_move_the_player()
    {
        var bad = new InputCommand { MoveZ = float.NaN, MoveX = float.PositiveInfinity, Yaw = float.NaN };
        Assert.Equal(Vector3.Zero, Simulation.Move(Vector3.Zero, bad, 1f, Open));
    }

    [Fact]
    public void Players_cannot_walk_through_buildings_or_leave_the_map()
    {
        var map = new MapLayout("box", 10, new[] { Aabb.FromCenter(0, 5, 4, 2, 3) });
        var forward = new InputCommand { MoveZ = 1 };
        Vector3 p = Vector3.Zero;
        for (int i = 0; i < 300; i++) p = Simulation.Move(p, forward, 1 / 30f, map);
        Assert.True(p.Z <= 4 - GameRules.PlayerRadius + 1e-3f, $"walked into the box: z={p.Z}");

        Vector3 q = new(0, 0, 9);
        for (int i = 0; i < 300; i++) q = Simulation.Move(q, new InputCommand { MoveX = 1 }, 1 / 30f, map);
        Assert.True(q.X <= 10 - GameRules.PlayerRadius + 1e-3f);
    }

    [Fact]
    public void Greybox_building_can_be_entered_through_the_door_only()
    {
        MapLayout map = MapLayout.GreyboxBlock(100);
        var north = new InputCommand { MoveZ = 1 };

        // Walking north from the south side, lined up with the doorway, ends up inside.
        Vector3 p = new(0, 0, -10);
        for (int i = 0; i < 120; i++) p = Simulation.Move(p, north, 1 / 30f, map);
        Assert.InRange(p.Z, -6f, 6f);

        // Two metres to the side of the doorway the south wall stops the player outside.
        Vector3 q = new(3, 0, -10);
        for (int i = 0; i < 120; i++) q = Simulation.Move(q, north, 1 / 30f, map);
        Assert.True(q.Z < -6f, $"passed through the wall: z={q.Z}");
    }

    [Fact]
    public void Ray_hits_capsule_in_front_and_misses_beside_it()
    {
        var eye = new Vector3(0, GameRules.EyeHeight, 0);
        Assert.NotNull(Simulation.RayCapsule(eye, Simulation.AimDirection(0, 0), new Vector3(0, 0, 20), 80));
        Assert.Null(Simulation.RayCapsule(eye, Simulation.AimDirection(0, 0), new Vector3(2, 0, 20), 80));
        Assert.Null(Simulation.RayCapsule(eye, Simulation.AimDirection(0, 0), new Vector3(0, 0, 100), 80));
    }

    [Fact]
    public void Ray_reports_wall_distance()
    {
        float? t = Simulation.RayAabb(new Vector3(0, 1.6f, 0), Vector3.UnitZ, Aabb.FromCenter(0, 10, 4, 2, 3), 80);
        Assert.NotNull(t);
        Assert.Equal(9f, t!.Value, 3);
    }
}
