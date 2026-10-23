#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using RWBR.Shared.Protocol;

namespace RWBR.Shared.Server
{
    /// <summary>
    /// Cars in the match: spawned at the start, entered and left with the Vehicle button, driven by whoever has the
    /// driver's seat, pushing people aside or running them over, damaged by crashes and grenades, and wrecked at zero
    /// health (throwing out and hurting whoever is inside). All server-authoritative.
    /// </summary>
    public sealed partial class Match
    {
        /// <summary>Damage to people inside, and to people next to it, when a car is wrecked.</summary>
        public const float WreckDamage = 40f;
        public const float WreckRadius = 4f;
        /// <summary>Frag damage to a car is this many times the damage it would do to a person at the same distance.</summary>
        public const float FragVehicleMultiplier = 2f;

        public sealed class Vehicle
        {
            public byte Id;
            public VehicleState State;
            public PlayerState?[] Seats = new PlayerState?[VehicleSim.Seats];
            public float Throttle, Steer;
            public bool Handbrake;

            public PlayerState? Driver => Seats[0];
        }

        private readonly List<Vehicle> _vehicles = new();

        public IReadOnlyList<Vehicle> Vehicles => _vehicles;

        private void SpawnVehicles()
        {
            _vehicles.Clear();
            if (!_cfg.Vehicles) return;
            foreach (var (position, yaw) in VehicleSpawns.For(_cfg.Map))
                _vehicles.Add(new Vehicle
                {
                    Id = (byte)_vehicles.Count,
                    State = new VehicleState { Position = position, Yaw = yaw, Health = VehicleSim.MaxHealth },
                });
        }

        /// <summary>The Vehicle button: get out if inside, else into the nearest free seat in reach (driver first).</summary>
        private void EnterOrExit(PlayerState p)
        {
            if (p.Vehicle != null)
            {
                Exit(p, hurt: true);
                return;
            }
            Vehicle? best = null;
            float bestDist = VehicleSim.EnterRange;
            foreach (Vehicle v in _vehicles)
            {
                if (v.State.Wrecked || Array.IndexOf(v.Seats, null) < 0) continue;
                float d = Vector2.Distance(new Vector2(p.Position.X, p.Position.Z), new Vector2(v.State.Position.X, v.State.Position.Z));
                if (d < bestDist && Math.Abs(p.Position.Y - v.State.Position.Y) < 1.5f)
                {
                    bestDist = d;
                    best = v;
                }
            }
            if (best == null) return;
            int seat = Array.IndexOf(best.Seats, null);
            best.Seats[seat] = p;
            p.Vehicle = best;
            p.Seat = seat;
            p.Combat.CancelHeal();
            SeatPlayer(p);
        }

        private void Exit(PlayerState p, bool hurt)
        {
            Vehicle? v = p.Vehicle;
            if (v == null) return;
            v.Seats[p.Seat] = null;
            p.Vehicle = null;
            Vector3 at = VehicleSim.ExitPosition(v.State, p.Seat, _cfg.Map);
            p.Motor = MotorState.StandingAt(at);
            float fast = Math.Abs(v.State.Speed) - VehicleSim.SafeExitSpeed;
            if (hurt && fast > 0 && p.Alive)
            {
                p.Combat.ApplyDamage(fast * VehicleSim.ExitDamagePerMps);
                if (!p.Combat.Alive) Eliminate(p, null);
            }
        }

        /// <summary>Puts an occupant on their seat (crouched, so they sit lower in the car).</summary>
        private void SeatPlayer(PlayerState p)
        {
            Vehicle v = p.Vehicle!;
            Vector3 seat = VehicleSim.SeatPosition(v.State, p.Seat);
            p.Motor.Position = new Vector3(seat.X, v.State.Position.Y + 0.1f, seat.Z);
            p.Motor.VerticalVelocity = 0;
            p.Motor.Grounded = true;
            p.Motor.Crouched = true;
        }

        /// <summary>One tick for every car: drive, crash, push people aside or run them over, carry its occupants.</summary>
        private void StepVehicles()
        {
            foreach (Vehicle v in _vehicles)
            {
                if (v.Driver == null || !v.Driver.Alive)
                {
                    v.Throttle = v.Steer = 0;
                    v.Handbrake = false;
                }
                bool wasWrecked = v.State.Wrecked;
                VehicleSim.Step(ref v.State, v.Throttle, v.Steer, v.Handbrake, _dt, _cfg.Map);
                HitPeople(v);
                if (!wasWrecked && v.State.Wrecked) Wreck(v);
                foreach (PlayerState? p in v.Seats)
                    if (p != null) SeatPlayer(p);
            }
        }

        private void HitPeople(Vehicle v)
        {
            float speed = Math.Abs(v.State.Speed);
            foreach (PlayerState p in _players)
            {
                if (!p.Alive || p.Vehicle != null || !VehicleSim.Touches(v.State, p.Position, GameRules.PlayerRadius)) continue;
                bool teammate = v.Driver != null && SameTeam(v.Driver, p);
                if (Math.Abs(p.Position.Y - v.State.Position.Y) > VehicleSim.Height) continue; // on a roof above it, or below a bridge
                if (speed >= VehicleSim.RunOverMinSpeed && !teammate)
                {
                    p.Combat.ApplyDamage(speed * VehicleSim.RunOverDamagePerMps);
                    p.Combat.CancelHeal();
                    v.State.Speed *= 0.7f;
                    if (!p.Combat.Alive)
                    {
                        PlayerState? driver = v.Driver;
                        Eliminate(p, driver != null && driver != p ? driver : null);
                        continue;
                    }
                }
                p.Motor.Position = Simulation.ResolveCollisions(VehicleSim.PushOut(v.State, p.Position), _cfg.Map);
            }
        }

        /// <summary>A car at zero health: everyone inside is thrown out and hurt, and so is anyone right next to it.</summary>
        private void Wreck(Vehicle v)
        {
            for (int s = 0; s < v.Seats.Length; s++)
            {
                PlayerState? p = v.Seats[s];
                if (p == null) continue;
                Exit(p, hurt: false);
            }
            foreach (PlayerState p in _players)
            {
                if (!p.Alive || Vector3.Distance(p.Position, v.State.Position) > WreckRadius) continue;
                p.Combat.ApplyDamage(WreckDamage);
                if (!p.Combat.Alive) Eliminate(p, null);
            }
        }

        private void DamageVehiclesFromBlast(Vector3 blast, PlayerState? owner)
        {
            foreach (Vehicle v in _vehicles)
            {
                if (v.State.Wrecked) continue;
                Vector3 centre = v.State.Position + new Vector3(0, VehicleSim.Height / 2, 0);
                float d = Math.Max(0, Vector3.Distance(blast, centre) - VehicleSim.CircleRadius);
                if (d > FragRadius) continue;
                v.State.Health = Math.Max(0, v.State.Health - FragDamage * FragVehicleMultiplier * (1f - d / FragRadius));
                if (v.State.Wrecked) Wreck(v);
            }
        }

        private void FillVehicles(SnapshotPacket snapshot)
        {
            foreach (Vehicle v in _vehicles)
                snapshot.Vehicles.Add(SnapshotCodec.Normalize(new VehicleEntry
                {
                    Id = v.Id,
                    X = v.State.Position.X,
                    Y = v.State.Position.Y,
                    Z = v.State.Position.Z,
                    Yaw = v.State.Yaw,
                    Speed = v.State.Speed,
                    Health = (byte)Math.Clamp(Math.Ceiling(v.State.Health / VehicleSim.MaxHealth * 100), 0, 100),
                    Driver = v.Seats[0]?.Id ?? ProtocolInfo.NoPlayer,
                    Passenger = v.Seats[1]?.Id ?? ProtocolInfo.NoPlayer,
                }));
        }
    }
}
