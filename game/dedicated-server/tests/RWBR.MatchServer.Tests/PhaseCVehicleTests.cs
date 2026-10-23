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

/// <summary>Phase C part 4: the car's driving rules.</summary>
public class VehicleSimTests
{
    private const float Dt = 1f / 30;
    private static readonly MapLayout Open = new("open", 200, Array.Empty<Aabb>());

    private static VehicleState Car(float x = 0, float z = 0, float yaw = 0) =>
        new() { Position = new Vector3(x, 0, z), Yaw = yaw, Health = VehicleSim.MaxHealth };

    private static void Drive(ref VehicleState v, float throttle, float steer, float seconds, MapLayout map, bool handbrake = false)
    {
        for (int i = 0; i < seconds * 30; i++) VehicleSim.Step(ref v, throttle, steer, handbrake, Dt, map);
    }

    [Fact]
    public void Accelerates_to_top_speed_brakes_and_reverses_slowly()
    {
        VehicleState v = Car();
        Drive(ref v, 1, 0, 1, Open);
        Assert.Equal(VehicleSim.Acceleration, v.Speed, 1);
        Drive(ref v, 1, 0, 6, Open);
        Assert.Equal(VehicleSim.MaxForwardSpeed, v.Speed, 2);
        Assert.True(v.Position.Z > 50);
        Drive(ref v, -1, 0, 1, Open);
        Assert.Equal(VehicleSim.MaxForwardSpeed - VehicleSim.BrakeDeceleration, v.Speed, 1); // brakes hard first
        Drive(ref v, -1, 0, 5, Open);
        Assert.Equal(-VehicleSim.MaxReverseSpeed, v.Speed, 2);
        Drive(ref v, 0, 0, 5, Open);
        Assert.Equal(0f, v.Speed); // rolls to a stop
    }

    [Fact]
    public void Steering_turns_tighter_when_slow_and_the_handbrake_stops_it()
    {
        VehicleState slow = Car(), fast = Car();
        slow.Speed = 5;
        fast.Speed = VehicleSim.MaxForwardSpeed;
        for (int i = 0; i < 15; i++)
        {
            VehicleSim.Step(ref slow, 0.3f, 1, false, Dt, Open);
            VehicleSim.Step(ref fast, 1, 1, false, Dt, Open);
        }
        float slowRadius = 15 * Dt * Math.Abs(slow.Speed) / Math.Abs(slow.Yaw);
        float fastRadius = 15 * Dt * Math.Abs(fast.Speed) / Math.Abs(fast.Yaw);
        Assert.True(slow.Yaw > 0 && fast.Yaw > 0, "steering right turns right");
        Assert.True(slowRadius < fastRadius, $"tighter when slow: {slowRadius:0.0} m vs {fastRadius:0.0} m");
        Drive(ref fast, 1, 0, 1.2f, Open, handbrake: true);
        Assert.Equal(0f, fast.Speed);
    }

    [Fact]
    public void A_wall_stops_the_car_and_damages_it()
    {
        var wall = new MapLayout("wall", 100, new[] { new Aabb(new Vector3(-10, 0, 20), new Vector3(10, 3, 21)) });
        VehicleState v = Car();
        v.Speed = 15;
        Drive(ref v, 1, 0, 3, wall);
        Assert.True(v.Position.Z + VehicleSim.CircleOffset + VehicleSim.CircleRadius <= 20.01f, $"in the wall at {v.Position}");
        Assert.True(v.Health < VehicleSim.MaxHealth, "crash damage");
        Assert.True(v.Health > 0);
    }

    [Fact]
    public void Kerbs_and_bridge_decks_are_driven_over_but_cover_walls_are_not()
    {
        var map = new MapLayout("kerbs", 100, new[]
        {
            new Aabb(new Vector3(-10, 0, 10), new Vector3(10, 0.2f, 12)),  // a kerb
            new Aabb(new Vector3(-10, 0, 30), new Vector3(10, 1.1f, 31)),  // a low wall
        });
        VehicleState v = Car();
        Drive(ref v, 1, 0, 3, map);
        Assert.True(v.Position.Z > 12, "over the kerb");
        Assert.True(v.Position.Z < 30, "stopped by the wall");
    }

    [Fact]
    public void The_river_is_crossed_by_car_only_on_a_bridge()
    {
        MapLayout v2 = CityMap.BuildV2();
        const float east = MathF.PI / 2;
        VehicleState onBridge = Car(CityMap.RiverWest - 20, 0, east);
        Drive(ref onBridge, 1, 0, 6, v2);
        Assert.True(onBridge.Position.X > CityMap.RiverEast + 5, $"across the middle bridge to {onBridge.Position}");
        VehicleState offBridge = Car(CityMap.RiverWest - 20, 100, east);
        Drive(ref offBridge, 1, 0, 4, v2);
        Assert.True(offBridge.Position.X < CityMap.RiverWest, $"into the river at {offBridge.Position}");
    }

    [Fact]
    public void Every_city_car_spawn_is_clear_and_on_the_road()
    {
        foreach (MapLayout map in new[] { CityMap.Build(), CityMap.BuildV2() })
        {
            var spawns = VehicleSpawns.For(map);
            Assert.NotEmpty(spawns);
            foreach (var (position, yaw) in spawns)
                Assert.Equal(position, VehicleSim.Resolve(position, yaw, map));
            foreach (Vector3 s in map.Spawns)
                Assert.DoesNotContain(spawns, c => Vector3.Distance(c.Position, s) < 4f); // no player starts inside a car
        }
        Assert.Empty(VehicleSpawns.For(MapLayout.GreyboxBlock(100f)));
    }
}

/// <summary>Phase C part 4: cars in a match.</summary>
public class MatchVehicleTests
{
    private static uint _seq = 9000;

    private static Match Started(out List<IPEndPoint> clients, int players = 3)
    {
        MapLayout city = CityMap.Build();
        var cfg = new MatchConfig
        {
            Map = city, MinPlayers = players, MaxPlayers = players, CountdownSeconds = 0.1f, StartWithWeapon = true,
            ZoneStages = new List<ZoneStage> { new(900, 1, 50) }, MaxMatchSeconds = 1000,
        };
        var m = new Match(cfg);
        clients = Enumerable.Range(0, players).Select(i => new IPEndPoint(IPAddress.Loopback, 9500 + i)).ToList();
        foreach (IPEndPoint c in clients)
        {
            byte[] hello = PacketCodec.Encode(new HelloPacket { Name = "v" });
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

    private static void Ticks(Match m, List<IPEndPoint> clients, int n, Dictionary<int, InputCommand>? cmds = null)
    {
        for (int i = 0; i < n; i++) Tick(m, clients, cmds);
    }

    [Fact]
    public void Two_people_get_in_drive_off_together_and_get_out_beside_it()
    {
        Match m = Started(out var clients);
        Match.Vehicle car = m.Vehicles[0];
        PlayerState driver = m.Players[0], passenger = m.Players[1];
        driver.Position = car.State.Position + new Vector3(-2, 0, 0);
        passenger.Position = car.State.Position + new Vector3(2, 0, 0);
        m.Players[2].Position = new Vector3(-200, 0, -200);

        Tick(m, clients, new() { [0] = new InputCommand { Buttons = InputButtons.Vehicle } });
        Tick(m, clients, new() { [1] = new InputCommand { Buttons = InputButtons.Vehicle } });
        Assert.Same(car, driver.Vehicle);
        Assert.Equal(0, driver.Seat);
        Assert.Equal(1, passenger.Seat);
        Vector3 start = car.State.Position;

        // The driver can't shoot; the passenger can.
        long shots = m.Stats.ShotsFired;
        Tick(m, clients, new() { [0] = new InputCommand { Buttons = InputButtons.Fire }, [1] = new InputCommand { Buttons = InputButtons.Fire } });
        Assert.Equal(shots + 1, m.Stats.ShotsFired);

        Ticks(m, clients, 60, new() { [0] = new InputCommand { MoveZ = 1 } });
        float moved = Vector3.Distance(start, car.State.Position);
        Assert.True(moved > 8, $"drove {moved:0.0} m");
        Assert.True(Vector2.Distance(new Vector2(passenger.Position.X, passenger.Position.Z), new Vector2(car.State.Position.X, car.State.Position.Z)) < 1.5f, "the passenger rides along");
        Assert.True(passenger.Motor.Crouched, "seated");

        // Get out while it is fast: it hurts.
        float before = driver.Health;
        Tick(m, clients, new() { [0] = new InputCommand { Buttons = InputButtons.Vehicle } });
        Assert.Null(driver.Vehicle);
        Assert.True(driver.Health < before, "jumping out at speed hurts");
        Assert.False(VehicleSim.Touches(car.State, driver.Position, 0f), "standing beside the car, not in it");
        Assert.Null(car.Driver);
        Ticks(m, clients, 240);
        Assert.Equal(0f, car.State.Speed); // nobody driving: it rolls to a stop
    }

    [Fact]
    public void Running_someone_over_at_speed_hurts_them_and_counts_for_the_driver()
    {
        Match m = Started(out var clients);
        Match.Vehicle car = m.Vehicles[0];
        PlayerState driver = m.Players[0], victim = m.Players[1];
        driver.Position = car.State.Position;
        m.Players[2].Position = new Vector3(-200, 0, -200);
        Tick(m, clients, new() { [0] = new InputCommand { Buttons = InputButtons.Vehicle } });
        victim.Position = car.State.Position + car.State.Forward * 25f;
        victim.Health = 50;
        for (int i = 0; i < 90 && victim.Alive; i++) Tick(m, clients, new() { [0] = new InputCommand { MoveZ = 1 } });
        Assert.False(victim.Alive, "hit at speed");
        Assert.Equal(1, driver.Kills);

        // A slow nudge only pushes people aside.
        PlayerState walker = m.Players[2];
        walker.Position = car.State.Position + car.State.Forward * 3.2f;
        car.State.Speed = 1.5f;
        Tick(m, clients);
        Assert.Equal(GameRules.MaxHealth, walker.Health);
        Assert.False(VehicleSim.Touches(car.State, walker.Position, 0f));
    }

    [Fact]
    public void A_frag_wrecks_a_car_and_throws_its_occupants_out()
    {
        Match m = Started(out var clients);
        Match.Vehicle car = m.Vehicles[0];
        PlayerState inside = m.Players[0], thrower = m.Players[1];
        inside.Position = car.State.Position;
        m.Players[2].Position = new Vector3(-200, 0, -200);
        Tick(m, clients, new() { [0] = new InputCommand { Buttons = InputButtons.Vehicle } });
        car.State.Health = 50;
        thrower.Position = car.State.Position + new Vector3(0, 0, -12);
        thrower.Combat.SetCount(ItemKind.Frag, 1);
        Tick(m, clients, new() { [1] = new InputCommand { Buttons = InputButtons.Throw, Item = ItemKind.Frag, Yaw = 0, Pitch = -0.15f } });
        Ticks(m, clients, (int)(Match.FragFuseSeconds * 30) + 3);
        Assert.True(car.State.Wrecked);
        Assert.Null(inside.Vehicle);
        Assert.True(inside.Health < GameRules.MaxHealth);
        Tick(m, clients, new() { [0] = new InputCommand { Buttons = InputButtons.Vehicle } });
        Assert.Null(inside.Vehicle); // a wreck can't be driven
    }

    [Fact]
    public void Parked_cars_cost_nothing_in_delta_snapshots_and_moving_ones_arrive()
    {
        var a = new SnapshotPacket { Tick = 1 };
        for (byte i = 0; i < 8; i++) a.Vehicles.Add(SnapshotCodec.Normalize(new VehicleEntry { Id = i, X = i * 10, Z = 5, Health = 100, Driver = ProtocolInfo.NoPlayer, Passenger = ProtocolInfo.NoPlayer }));
        var history = new SnapshotHistory();
        byte[] full = SnapshotCodec.Encode(a, null);
        SnapshotPacket gotA = SnapshotCodec.Decode(new PacketReader(full, full.Length), history);
        history.Add(gotA);
        Assert.Equal(8, gotA.Vehicles.Count);

        var b = new SnapshotPacket { Tick = 2 };
        b.Vehicles.AddRange(a.Vehicles);
        byte[] parked = SnapshotCodec.Encode(b, a);
        var c = new SnapshotPacket { Tick = 2 };
        c.Vehicles.AddRange(a.Vehicles);
        c.Vehicles[3] = SnapshotCodec.Normalize(new VehicleEntry { Id = 3, X = 30.5f, Z = 6, Yaw = 0.3f, Speed = 12, Health = 90, Driver = 4, Passenger = ProtocolInfo.NoPlayer });
        byte[] moving = SnapshotCodec.Encode(c, a);
        Assert.True(full.Length - parked.Length > 8 * 15, "full snapshot carries all cars");
        SnapshotPacket gotC = SnapshotCodec.Decode(new PacketReader(moving, moving.Length), history);
        Assert.Equal(8, gotC.Vehicles.Count);
        VehicleEntry car = gotC.Vehicles.Single(v => v.Id == 3);
        Assert.Equal(30.5f, car.X, 2);
        Assert.Equal(12f, car.Speed, 1);
        Assert.Equal(4, car.Driver);
        Assert.Equal(0f, gotC.Vehicles.Single(v => v.Id == 5).Speed);
        Assert.True(moving.Length - parked.Length < 20, $"one moving car costs {moving.Length - parked.Length} bytes");
    }
}
