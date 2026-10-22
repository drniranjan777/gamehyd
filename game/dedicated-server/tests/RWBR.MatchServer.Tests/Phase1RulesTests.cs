using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using Xunit;

namespace RWBR.MatchServer.Tests;

public class MotorTests
{
    private const float Dt = 1 / 30f;
    private static readonly MapLayout Open = new("open", 100, Array.Empty<Aabb>());

    private static void Run(ref MotorState s, InputCommand cmd, int ticks, MapLayout map)
    {
        for (int i = 0; i < ticks; i++) Simulation.Step(ref s, cmd, Dt, map);
    }

    [Fact]
    public void Jump_reaches_about_one_metre_and_lands()
    {
        MotorState s = MotorState.StandingAt(Vector3.Zero);
        Simulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, Open);
        float peak = 0;
        for (int i = 0; i < 60; i++)
        {
            Simulation.Step(ref s, new InputCommand(), Dt, Open);
            peak = Math.Max(peak, s.Position.Y);
        }
        Assert.InRange(peak, 0.85f, 1.1f);
        Assert.True(s.Grounded);
        Assert.Equal(0f, s.Position.Y);
    }

    [Fact]
    public void Holding_jump_in_the_air_does_not_gain_height()
    {
        MotorState s = MotorState.StandingAt(Vector3.Zero);
        float peak = 0;
        for (int i = 0; i < 90; i++)
        {
            Simulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, Open);
            peak = Math.Max(peak, s.Position.Y);
        }
        Assert.True(peak < 1.1f, $"peak {peak}");
    }

    [Fact]
    public void Stairs_lead_to_the_roof_and_walking_off_the_roof_falls_to_the_ground()
    {
        MapLayout map = MapLayout.GreyboxBlock(100);
        // East-side stairs start at the south-east corner of the building (x 8..9.5).
        MotorState s = MotorState.StandingAt(new Vector3(8.75f, 0, -9));
        Run(ref s, new InputCommand { MoveZ = 1 }, 75, map); // north, up the stairs
        Assert.True(s.Position.Y > MapLayout.RoofTop - 0.5f, $"top of stairs y={s.Position.Y}");

        float west = -MathF.PI / 2;
        Run(ref s, new InputCommand { MoveZ = 1, Yaw = west }, 30, map); // onto the roof
        Assert.Equal(MapLayout.RoofTop, s.Position.Y, 2);
        Assert.InRange(s.Position.X, -8f, 8f);

        Run(ref s, new InputCommand { MoveZ = 1 }, 60, map); // north, off the roof edge
        Assert.True(s.Grounded);
        Assert.Equal(0f, s.Position.Y, 3);
        Assert.True(s.Position.Z > 6f);
    }

    [Fact]
    public void Walls_cannot_be_climbed_without_stairs()
    {
        MapLayout map = MapLayout.GreyboxBlock(100);
        MotorState s = MotorState.StandingAt(new Vector3(0, 0, 10)); // north of the building
        Run(ref s, new InputCommand { MoveZ = -1, Yaw = 0, Buttons = InputButtons.Jump }, 120, map); // jump into the wall
        Assert.True(s.Position.Z >= 6f + GameRules.PlayerRadius - 0.01f, $"z={s.Position.Z}");
        Assert.True(s.Position.Y < 1.2f);
    }

    [Fact]
    public void Crouch_is_slower_and_cannot_stand_under_a_low_ceiling()
    {
        var map = new MapLayout("low", 50, new[] { new Aabb(new Vector3(-2, 1.5f, -2), new Vector3(2, 2, 2)) });
        MotorState s = MotorState.StandingAt(new Vector3(0, 0, -6));
        Run(ref s, new InputCommand { MoveZ = 1, Buttons = InputButtons.Crouch }, 30, map);
        Assert.InRange(s.Position.Z, -6 + GameRules.CrouchSpeed - 0.05f, -6 + GameRules.CrouchSpeed + 0.05f);

        Run(ref s, new InputCommand { MoveZ = 1, Buttons = InputButtons.Crouch }, 30, map); // now under the slab
        Run(ref s, new InputCommand(), 5, map);                                           // release crouch
        Assert.True(s.Crouched, "stood up into the ceiling");
        Assert.Equal(GameRules.CrouchHeight, s.Height);
    }

    [Fact]
    public void Spread_is_deterministic_and_bounded()
    {
        Vector3 a = Simulation.SpreadDirection(0.3f, 0.1f, 2f, 1234);
        Vector3 b = Simulation.SpreadDirection(0.3f, 0.1f, 2f, 1234);
        Assert.Equal(a, b);
        for (uint seed = 0; seed < 500; seed++)
        {
            Vector3 d = Simulation.SpreadDirection(0, 0, 2f, seed);
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(d), Vector3.UnitZ), -1, 1)) * 180 / MathF.PI;
            Assert.True(angle <= 2.01f, $"seed {seed}: {angle}°");
        }
    }
}

public class CombatantTests
{
    private static readonly WeaponDefinition Rifle = WeaponDefinition.DefaultRifle();

    [Fact]
    public void Armor_absorbs_half_until_depleted()
    {
        var c = new Combatant { Armor = 10 };
        Assert.Equal(10f, c.ApplyDamage(20));
        Assert.Equal(0f, c.Armor);
        Assert.Equal(90f, c.Health);
        Assert.Equal(20f, c.ApplyDamage(20));
        Assert.Equal(70f, c.Health);
    }

    [Fact]
    public void Medkit_heals_after_its_duration_and_is_consumed()
    {
        var c = new Combatant { Health = 30, Medkits = 1 };
        Assert.True(c.TryStartHeal(0, 30));
        Assert.False(c.TryStartHeal(1, 30));
        c.Update(89, Rifle);
        Assert.Equal(30f, c.Health);
        c.Update(90, Rifle);
        Assert.Equal(80f, c.Health);
        Assert.Equal(0, c.Medkits);
        Assert.False(c.TryStartHeal(91, 30));
    }

    [Fact]
    public void Reload_draws_from_reserve_and_blocks_firing()
    {
        var c = new Combatant();
        c.Reset(Rifle, withWeapon: true, reserveAmmo: 10);
        for (int i = 0; i < 5; i++) Assert.Equal(FireResult.Fired, c.TryFire(i * 10, Rifle, 30));
        Assert.True(c.TryStartReload(100, Rifle, 30));
        Assert.Equal(FireResult.Reloading, c.TryFire(101, Rifle, 30));
        c.Update(160, Rifle);
        Assert.Equal(30, c.Ammo);
        Assert.Equal(5, c.ReserveAmmo);
    }

    [Fact]
    public void Unarmed_players_cannot_fire_and_pickups_respect_caps()
    {
        var c = new Combatant();
        c.Reset(Rifle, withWeapon: false, reserveAmmo: 0);
        Assert.Equal(FireResult.NoWeapon, c.TryFire(0, Rifle, 30));

        var rifle = new LootSpawn(0, LootType.Rifle, Vector3.Zero, 1);
        Assert.True(c.CanPickUp(rifle));
        c.PickUp(rifle, Rifle);
        Assert.False(c.CanPickUp(rifle));
        Assert.Equal(Rifle.MagazineSize, c.Ammo);

        var vest = new LootSpawn(1, LootType.Armor, Vector3.Zero, 50);
        c.PickUp(vest, Rifle);
        Assert.False(c.CanPickUp(vest));
        Assert.True(c.CanPickUp(new LootSpawn(2, LootType.Armor, Vector3.Zero, 100)));
    }
}

public class MatchPhase1Tests
{
    private static Match StartedMatch(out List<IPEndPoint> clients, MapLayout map, int players = 2)
    {
        MatchConfig cfg = MatchConfig.Fast(players);
        cfg.Map = map;
        var match = new Match(cfg);
        clients = Enumerable.Range(0, players).Select(i => new IPEndPoint(IPAddress.Loopback, 9000 + i)).ToList();
        foreach (IPEndPoint c in clients)
        {
            byte[] hello = PacketCodec.Encode(new HelloPacket { Name = "t" });
            match.Receive(c, hello, hello.Length);
        }
        uint seq = 0;
        while (match.Phase != MatchPhase.Active)
        {
            seq++;
            foreach (IPEndPoint c in clients) Send(match, c, new InputCommand { Sequence = seq });
            match.Advance();
            match.Outbox.Clear();
        }
        return match;
    }

    private static void Send(Match m, IPEndPoint from, InputCommand cmd)
    {
        byte[] data = PacketCodec.Encode(new List<InputCommand> { cmd });
        m.Receive(from, data, data.Length);
    }

    private static PlayerState Player(Match m, int index) => m.Players[index];

    private static MapLayout LootMap() => new("loot", 40, Array.Empty<Aabb>(), new[]
    {
        new LootSpawn(0, LootType.Medkit, new Vector3(0, 0, 0), 1),
        new LootSpawn(1, LootType.Armor, new Vector3(10, 0, 0), 100),
    });

    [Fact]
    public void Pickup_requires_range_and_each_item_can_be_taken_once()
    {
        Match m = StartedMatch(out var clients, LootMap());
        PlayerState a = Player(m, 0), b = Player(m, 1);
        a.Position = new Vector3(5, 0, 0); // out of range of both items
        b.Position = new Vector3(1, 0, 0); // in range of the medkit
        uint seq = 1000;

        Send(m, clients[0], new InputCommand { Sequence = ++seq, Buttons = InputButtons.Interact });
        Send(m, clients[1], new InputCommand { Sequence = ++seq, Buttons = InputButtons.Interact });
        m.Advance();
        Assert.Equal(0, a.Combat.Medkits);
        Assert.Equal(1, b.Combat.Medkits);

        a.Position = new Vector3(0.5f, 0, 0); // now in range, but the medkit is gone
        Send(m, clients[0], new InputCommand { Sequence = ++seq, Buttons = InputButtons.Interact });
        m.Advance();
        Assert.Equal(0, a.Combat.Medkits);
        Assert.Equal(1, m.Stats.Pickups);
        Assert.Equal(2, m.Stats.PickupsRejected);

        byte[] snap = m.Outbox.Last(o => o.To.Equals(clients[0])).Data;
        SnapshotPacket s = PacketCodec.DecodeSnapshot(new PacketReader(snap, snap.Length));
        Assert.True(s.IsLootTaken(0));
        Assert.False(s.IsLootTaken(1));
    }

    [Fact]
    public void Shots_over_a_crouched_target_miss()
    {
        Match m = StartedMatch(out var clients, new MapLayout("open", 40, Array.Empty<Aabb>()));
        PlayerState shooter = Player(m, 0), target = Player(m, 1);
        shooter.Position = new Vector3(0, 0, -10);
        target.Motor = MotorState.StandingAt(new Vector3(0, 0, 0));
        target.Motor.Crouched = true;
        Array.Fill(target.History, target.Motor);

        // Aim level at head height of a standing player (1.6 m): passes over a 1.2 m crouched capsule.
        uint seq = 2000;
        for (int i = 0; i < 20; i++)
        {
            Send(m, clients[0], new InputCommand { Sequence = ++seq, Yaw = 0, Pitch = 0, Buttons = InputButtons.Fire, AckTick = (uint)m.Tick });
            Send(m, clients[1], new InputCommand { Sequence = ++seq, Buttons = InputButtons.Crouch });
            m.Advance();
            m.Outbox.Clear();
        }
        Assert.True(m.Stats.ShotsFired > 0);
        Assert.Equal(0, m.Stats.Hits);
        Assert.Equal(GameRules.MaxHealth, target.Health);
    }

    [Fact]
    public void Taking_damage_interrupts_healing()
    {
        Match m = StartedMatch(out var clients, new MapLayout("open", 40, Array.Empty<Aabb>()));
        PlayerState shooter = Player(m, 0), target = Player(m, 1);
        shooter.Position = new Vector3(0, 0, -10);
        target.Motor = MotorState.StandingAt(Vector3.Zero);
        Array.Fill(target.History, target.Motor);
        target.Health = 40;
        target.Combat.Medkits = 1;

        uint seq = 3000;
        Send(m, clients[1], new InputCommand { Sequence = ++seq, Buttons = InputButtons.Heal });
        m.Advance();
        Assert.True(target.Combat.Healing);

        Send(m, clients[0], new InputCommand { Sequence = ++seq, Yaw = 0, Pitch = -0.05f, Buttons = InputButtons.Fire, AckTick = (uint)m.Tick });
        m.Advance();
        Assert.Equal(1, m.Stats.Hits);
        Assert.False(target.Combat.Healing);
        Assert.Equal(1, target.Combat.Medkits);
    }
}
