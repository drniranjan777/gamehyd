#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace RWBR.Shared
{
    /// <summary>One car's driving state. A struct so it can be stepped by reference like <see cref="MotorState"/>.</summary>
    public struct VehicleState
    {
        public Vector3 Position;
        /// <summary>Heading in radians (0 = +Z, like player yaw).</summary>
        public float Yaw;
        /// <summary>Signed speed along the heading, m/s (negative = reversing).</summary>
        public float Speed;
        public float Health;

        public bool Wrecked => Health <= 0;
        public Vector3 Forward => Simulation.Forward(Yaw);
    }

    /// <summary>
    /// The car (one model for now), shared by the server and the client so both drive it with the same rules. A
    /// kinematic "bicycle" model rather than a rigid body: throttle accelerates along the heading, steering turns at a
    /// rate set by speed and the wheelbase (less lock at speed), the handbrake stops it hard, and it rolls to a stop on its
    /// own. For the world the car is two circles along its length; anything taller than a kerb stops it, it bounces back
    /// and takes damage. Water always stops it. It drives on the ground only (no ramps or roofs).
    /// </summary>
    public static class VehicleSim
    {
        public const float MaxForwardSpeed = 22f;   // about 80 km/h
        public const float MaxReverseSpeed = 6f;
        public const float Acceleration = 6f;
        public const float BrakeDeceleration = 14f;
        public const float HandbrakeDeceleration = 20f;
        public const float RollingDrag = 2f;
        public const float Wheelbase = 2.6f;
        public const float MaxSteerRadians = 0.52f; // 30 degrees at a standstill
        public const float MinSteerRadians = 0.12f; // about 7 degrees at top speed
        public const float Length = 4.4f;
        public const float Width = 2.0f;
        public const float Height = 1.5f;
        /// <summary>The two collision circles sit this far ahead of and behind the centre.</summary>
        public const float CircleOffset = 1.2f;
        public const float CircleRadius = 1.05f;
        /// <summary>Obstacles up to this height are driven over (kerbs, bridge decks).</summary>
        public const float KerbHeight = 0.3f;
        public const float MaxHealth = 600f;
        /// <summary>Damage to the car per m/s of speed lost against a wall.</summary>
        public const float CrashDamagePerMps = 6f;
        /// <summary>Hitting a person at this speed or more hurts them: <see cref="RunOverDamagePerMps"/> per m/s.</summary>
        public const float RunOverMinSpeed = 4f;
        public const float RunOverDamagePerMps = 4.5f;
        /// <summary>Getting out faster than this hurts: per m/s over it.</summary>
        public const float SafeExitSpeed = 6f;
        public const float ExitDamagePerMps = 4f;
        public const float EnterRange = 3.2f;
        public const int Seats = 2;

        /// <summary>Damage the car took this step from hitting something (0 when it didn't).</summary>
        public static float Step(ref VehicleState v, float throttle, float steer, bool handbrake, float dt, MapLayout map)
        {
            if (v.Wrecked)
            {
                v.Speed = 0;
                return 0;
            }
            throttle = Math.Clamp(Simulation.SanitizeAngle(throttle, 1f), -1f, 1f);
            steer = Math.Clamp(Simulation.SanitizeAngle(steer, 1f), -1f, 1f);

            // Throttle and brakes. Pulling back while rolling forward brakes; once stopped it reverses.
            float s = v.Speed;
            if (handbrake) s = MoveTowards(s, 0, HandbrakeDeceleration * dt);
            else if (throttle > 0.01f)
                s = s < 0 ? MoveTowards(s, 0, BrakeDeceleration * dt * throttle) : Math.Min(MaxForwardSpeed, s + Acceleration * throttle * dt);
            else if (throttle < -0.01f)
                s = s > 0 ? MoveTowards(s, 0, BrakeDeceleration * dt * -throttle) : Math.Max(-MaxReverseSpeed, s + Acceleration * throttle * dt);
            else s = MoveTowards(s, 0, RollingDrag * dt);
            v.Speed = s;

            // Steering: less lock the faster it goes; reversing steers the other way round.
            float lockAngle = MaxSteerRadians + (MinSteerRadians - MaxSteerRadians) * Math.Min(1f, Math.Abs(s) / MaxForwardSpeed);
            v.Yaw = Simulation.WrapYaw(v.Yaw + s / Wheelbase * (float)Math.Tan(steer * lockAngle) * dt);

            Vector3 wanted = v.Position + v.Forward * (s * dt);
            Vector3 resolved = Resolve(wanted, v.Yaw, map);
            float damage = 0;
            float blocked = Vector2.Distance(new Vector2(wanted.X, wanted.Z), new Vector2(resolved.X, resolved.Z));
            if (blocked > 0.01f && Math.Abs(s) > 0.5f)
            {
                // Hit something: bounce back a little and take damage from the speed lost.
                damage = Math.Abs(s) * CrashDamagePerMps;
                v.Speed = -s * 0.25f;
                v.Health = Math.Max(0, v.Health - damage);
            }
            resolved.Y = Simulation.GroundHeight(resolved, resolved.Y + KerbHeight, map);
            v.Position = resolved;
            return damage;
        }

        /// <summary>Pushes the car's two circles out of every box taller than a kerb (and all water).</summary>
        public static Vector3 Resolve(Vector3 centre, float yaw, MapLayout map)
        {
            Vector3 f = Simulation.Forward(yaw) * CircleOffset;
            Vector3 p = centre;
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (float side in new[] { 1f, -1f })
                {
                    Vector3 c = p + f * side;
                    Vector3 pushed = PushCircle(c, p.Y, map);
                    p += pushed - c;
                }
            }
            float limit = map.HalfSize - CircleOffset - CircleRadius;
            p.X = Math.Clamp(p.X, -limit, limit);
            p.Z = Math.Clamp(p.Z, -limit, limit);
            return p;
        }

        [ThreadStatic] private static List<int>? _near;

        private static Vector3 PushCircle(Vector3 c, float floorY, MapLayout map)
        {
            float r = CircleRadius;
            List<int> near = _near ??= new List<int>();
            map.Grid.Query(c.X - r, c.Z - r, c.X + r, c.Z + r, near);
            foreach (int i in near)
            {
                Aabb b = map.Obstacles[i];
                bool water = map.KindOf(i) == BoxKind.Water;
                if (!water && (b.Max.Y <= floorY + KerbHeight || b.Min.Y >= floorY + Height)) continue;
                // Closest point of the box to the circle centre, in the ground plane.
                float qx = Math.Clamp(c.X, b.Min.X, b.Max.X), qz = Math.Clamp(c.Z, b.Min.Z, b.Max.Z);
                float dx = c.X - qx, dz = c.Z - qz;
                float d2 = dx * dx + dz * dz;
                if (d2 >= r * r) continue;
                if (d2 > 1e-8f)
                {
                    float d = (float)Math.Sqrt(d2);
                    c.X = qx + dx / d * r;
                    c.Z = qz + dz / d * r;
                }
                else
                {
                    // Centre inside the box: out the nearest side.
                    float left = c.X - b.Min.X, right = b.Max.X - c.X, back = c.Z - b.Min.Z, front = b.Max.Z - c.Z;
                    float m = Math.Min(Math.Min(left, right), Math.Min(back, front));
                    if (m == left) c.X = b.Min.X - r;
                    else if (m == right) c.X = b.Max.X + r;
                    else if (m == back) c.Z = b.Min.Z - r;
                    else c.Z = b.Max.Z + r;
                }
            }
            return c;
        }

        /// <summary>Whether a point on the ground is within the car's footprint (its two circles), with a margin.</summary>
        public static bool Touches(in VehicleState v, Vector3 p, float margin)
        {
            Vector3 f = v.Forward * CircleOffset;
            float r = CircleRadius + margin;
            foreach (float side in new[] { 1f, -1f })
            {
                Vector3 c = v.Position + f * side;
                if (new Vector2(p.X - c.X, p.Z - c.Z).LengthSquared() < r * r) return true;
            }
            return false;
        }

        /// <summary>Pushes a standing player (radius <see cref="GameRules.PlayerRadius"/>) out of the car's footprint.</summary>
        public static Vector3 PushOut(in VehicleState v, Vector3 p)
        {
            Vector3 f = v.Forward * CircleOffset;
            float r = CircleRadius + GameRules.PlayerRadius;
            foreach (float side in new[] { 1f, -1f })
            {
                Vector3 c = v.Position + f * side;
                var d = new Vector2(p.X - c.X, p.Z - c.Z);
                float len = d.Length();
                if (len >= r) continue;
                Vector2 dir = len > 1e-4f ? d / len : new Vector2((float)Math.Cos(v.Yaw), -(float)Math.Sin(v.Yaw));
                p.X = c.X + dir.X * r;
                p.Z = c.Z + dir.Y * r;
            }
            return p;
        }

        /// <summary>Where seat <paramref name="seat"/> is (0 driver on the left, 1 passenger on the right), at hip height.</summary>
        public static Vector3 SeatPosition(in VehicleState v, int seat)
        {
            Vector3 right = Simulation.Right(v.Yaw);
            return v.Position + right * (seat == 0 ? -0.45f : 0.45f) + v.Forward * 0.2f + new Vector3(0, 0.4f, 0);
        }

        /// <summary>Where someone getting out of a seat stands: beside their door, or on the other side if that's blocked.</summary>
        public static Vector3 ExitPosition(in VehicleState v, int seat, MapLayout map)
        {
            Vector3 right = Simulation.Right(v.Yaw);
            foreach (float side in seat == 0 ? new[] { -1f, 1f } : new[] { 1f, -1f })
            {
                Vector3 p = new Vector3(v.Position.X, 0, v.Position.Z) + right * (side * (Width / 2 + GameRules.PlayerRadius + 0.3f));
                p.Y = Simulation.GroundHeight(p, v.Position.Y + GameRules.StepHeight, map);
                if (Simulation.ResolveCollisions(p, map) == p) return p;
            }
            return new Vector3(v.Position.X, v.Position.Y, v.Position.Z) + v.Forward * (Length / 2 + 0.8f);
        }

        private static float MoveTowards(float v, float target, float maxDelta) =>
            Math.Abs(target - v) <= maxDelta ? target : v + Math.Sign(target - v) * maxDelta;
    }

    /// <summary>Where cars are parked at the start of a match, per map version (none on the small maps).</summary>
    public static class VehicleSpawns
    {
        public static IReadOnlyList<(Vector3 Position, float Yaw)> For(MapLayout map)
        {
            var list = new List<(Vector3, float)>();
            if (!map.Version.StartsWith("hyd-city", StringComparison.Ordinal)) return list;
            const float north = 0f, east = (float)(Math.PI / 2);
            // Along the two avenues, parked at the kerb, clear of the plaza and the player spawns on the centre line.
            foreach (float t in new[] { -240f, -165f, 105f, 195f })
            {
                list.Add((new Vector3(6.5f, 0, t), north));
                list.Add((new Vector3(t + 7, 0, -6.5f), east));
            }
            if (map.HalfSize > CityMap.HalfSize)
            {
                // The ring: out by the warehouses, the fields, the logistics park and across the river.
                list.Add((new Vector3(-6.5f, 0, 380), north));
                list.Add((new Vector3(6.5f, 0, -400), north));
                list.Add((new Vector3(-400, 0, 6.5f), east));
                list.Add((new Vector3(CityMap.RiverEast + 20, 0, 6.5f), east));
            }
            return list;
        }
    }
}
