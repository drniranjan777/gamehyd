using System;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using Xunit;

namespace RWBR.MatchServer.Tests;

/// <summary>Phase B: aiming down sights is a server-validated input (protocol v5, balance v4).</summary>
public class AimTests
{
    private const float Dt = 1 / 30f;
    private static readonly MapLayout Open = new("open", 100, Array.Empty<Aabb>());

    [Fact]
    public void Aim_bit_survives_the_input_packet()
    {
        var sent = new InputCommand { Sequence = 7, MoveZ = 1, Buttons = InputButtons.Aim | InputButtons.Fire | InputButtons.SwitchWeapon };
        byte[] data = PacketCodec.Encode(new[] { sent });
        var r = new PacketReader(data, data.Length);
        Assert.Equal(PacketType.Input, r.Type);
        InputCommand got = PacketCodec.DecodeInput(r)[0];
        Assert.Equal(sent.Buttons, got.Buttons);
        Assert.True(got.Has(InputButtons.Aim));
        Assert.True(ProtocolInfo.Version >= 5, "aim needs protocol v5 or later");
    }

    [Fact]
    public void Pitch_and_aiming_reach_other_players_through_delta_snapshots()
    {
        Assert.True(ProtocolInfo.Version >= 6, "pitch and aim arrived in protocol 6");
        var history = new SnapshotHistory();
        SnapshotEntry Entry(float pitch, PlayerFlags flags) => Quantize.Normalize(new SnapshotEntry
        {
            PlayerId = 3, X = 5, Z = 5, Yaw = 1f, Pitch = pitch, Health = 100, Flags = PlayerFlags.Alive | flags,
        });
        SnapshotPacket RoundTrip(uint tick, SnapshotEntry e, SnapshotPacket? baseline)
        {
            var p = new SnapshotPacket { Tick = tick, Phase = MatchPhase.Active };
            p.Players.Add(e);
            byte[] bytes = SnapshotCodec.Encode(p, baseline);
            SnapshotPacket got = SnapshotCodec.Decode(new PacketReader(bytes, bytes.Length), history);
            history.Add(got);
            return got;
        }

        SnapshotPacket full = RoundTrip(1, Entry(-0.3f, PlayerFlags.None), null);
        Assert.InRange(full.Players[0].Pitch, -0.31f, -0.29f); // within half a degree
        Assert.Equal(0, (int)(full.Players[0].Flags & PlayerFlags.Aiming));

        // Only the view and the flags change: the delta carries both.
        SnapshotPacket delta = RoundTrip(2, Entry(0.5f, PlayerFlags.Aiming), full);
        Assert.InRange(delta.Players[0].Pitch, 0.49f, 0.51f);
        Assert.True((delta.Players[0].Flags & PlayerFlags.Aiming) != 0);
        Assert.Equal(full.Players[0].Yaw, delta.Players[0].Yaw);

        // Pitch is clamped to the wire range and kept to one degree.
        Assert.Equal(90f * MathF.PI / 180f, Quantize.Pitch(Quantize.Pitch(3f)), 4);
        Assert.Equal(MathF.Round(0.4f * 180f / MathF.PI) * MathF.PI / 180f, Quantize.Pitch(Quantize.Pitch(0.4f)), 4);
    }

    [Fact]
    public void Aiming_tightens_spread_for_every_weapon_but_not_airborne_spread()
    {
        foreach (WeaponDefinition w in WeaponCatalog.All)
        {
            Assert.InRange(w.AimSpreadMultiplier, 0.05f, 1f);
            Assert.InRange(w.AimZoom, 1f, 8f);
            Assert.True(w.SpreadFor(false, false, false, aiming: true) < w.SpreadFor(false, false, false), w.Id);
            Assert.True(w.SpreadFor(true, false, false, aiming: true) < w.SpreadFor(true, false, false), w.Id);
            float airborneExtra = w.SpreadFor(false, false, true, aiming: true) - w.SpreadFor(false, false, false, aiming: true);
            Assert.Equal(w.AirborneSpreadDegrees, airborneExtra, 3);
        }
        Assert.True(WeaponCatalog.Get(WeaponCatalog.Sniper).AimZoom >= 3, "the sniper uses a scope");
    }

    [Fact]
    public void Aiming_slows_movement_and_blocks_sprint()
    {
        float Distance(InputButtons buttons)
        {
            MotorState s = MotorState.StandingAt(Vector3.Zero);
            for (int i = 0; i < 30; i++) Simulation.Step(ref s, new InputCommand { MoveZ = 1, Buttons = buttons }, Dt, Open);
            return s.Position.Length();
        }
        Assert.Equal(GameRules.WalkSpeed, Distance(InputButtons.None), 1);
        Assert.Equal(GameRules.SprintSpeed, Distance(InputButtons.Sprint), 1);
        Assert.Equal(GameRules.WalkSpeed * GameRules.AimMoveSpeedFactor, Distance(InputButtons.Aim), 1);
        Assert.Equal(GameRules.WalkSpeed * GameRules.AimMoveSpeedFactor, Distance(InputButtons.Aim | InputButtons.Sprint), 1);
    }
}
