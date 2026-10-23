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
        Assert.Equal((ushort)5, ProtocolInfo.Version);
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
