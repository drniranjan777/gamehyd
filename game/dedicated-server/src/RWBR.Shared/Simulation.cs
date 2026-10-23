#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace RWBR.Shared
{
    /// <summary>Deterministic movement and hit geometry, shared by server authority and client prediction.</summary>
    public static class Simulation
    {
        /// <summary>Ledge drop that still counts as walking (stairs down); larger drops start a fall.</summary>
        private const float StepDown = 0.45f;
        /// <summary>Footprint inset used for standing on ledges, so players can stand partly over an edge.</summary>
        private const float FootInset = 0.7f;

        [ThreadStatic] private static List<int>? _near;

        /// <summary>Obstacles near an XZ rectangle, from the map's spatial grid (per-thread scratch list).</summary>
        private static List<int> Near(MapLayout map, float minX, float minZ, float maxX, float maxZ)
        {
            List<int> list = _near ??= new List<int>(64);
            map.Grid.Query(minX, minZ, maxX, maxZ, list);
            return list;
        }

        public static float SanitizeAngle(float radians, float limit)
        {
            if (float.IsNaN(radians) || float.IsInfinity(radians)) return 0f;
            return Math.Max(-limit, Math.Min(limit, radians));
        }

        public static float WrapYaw(float yaw)
        {
            if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return 0f;
            double twoPi = 2 * Math.PI;
            double w = yaw % twoPi;
            if (w < -Math.PI) w += twoPi;
            if (w > Math.PI) w -= twoPi;
            return (float)w;
        }

        public static Vector3 Forward(float yaw) => new Vector3((float)Math.Sin(yaw), 0, (float)Math.Cos(yaw));

        public static Vector3 Right(float yaw) => new Vector3((float)Math.Cos(yaw), 0, -(float)Math.Sin(yaw));

        public static Vector3 AimDirection(float yaw, float pitch)
        {
            float cp = (float)Math.Cos(pitch);
            return new Vector3(cp * (float)Math.Sin(yaw), (float)Math.Sin(pitch), cp * (float)Math.Cos(yaw));
        }

        /// <summary>
        /// Advances a character by one tick: walk/sprint/crouch, jump, gravity, stairs, walls and ceilings.
        /// Oversized or non-finite input is clamped, so a tampered client can never move faster than
        /// <see cref="GameRules.SprintSpeed"/> or jump while airborne.
        /// </summary>
        public static void Step(ref MotorState s, InputCommand input, float dt, MapLayout map)
        {
            float mx = Finite(input.MoveX), mz = Finite(input.MoveZ);
            float len = (float)Math.Sqrt(mx * mx + mz * mz);
            if (len > 1f)
            {
                mx /= len;
                mz /= len;
            }
            if (s.Drop == DropPhase.Plane) return; // the match carries everyone aboard
            if (s.Drop != DropPhase.None)
            {
                StepDrop(ref s, input, mx, mz, dt, map);
                return;
            }

            if (input.Has(InputButtons.Crouch)) s.Crouched = true;
            else if (s.Crouched && HasHeadroom(s.Position, GameRules.PlayerHeight, map)) s.Crouched = false;

            float yaw = WrapYaw(input.Yaw);
            bool aiming = input.Has(InputButtons.Aim);
            bool sprint = input.Has(InputButtons.Sprint) && mz > 0.5f && !s.Crouched && !aiming;
            float speed = s.Crouched ? GameRules.CrouchSpeed : sprint ? GameRules.SprintSpeed : GameRules.WalkSpeed;
            if (aiming) speed *= GameRules.AimMoveSpeedFactor;
            Vector3 p = s.Position + (Right(yaw) * mx + Forward(yaw) * mz) * speed * dt;
            p = ResolveHorizontal(p, s.Position.Y, s.Height, map);

            if (s.Grounded && input.Has(InputButtons.Jump) && !s.Crouched)
            {
                s.VerticalVelocity = GameRules.JumpSpeed;
                s.Grounded = false;
            }

            if (!s.Grounded)
            {
                s.VerticalVelocity = Math.Max(-GameRules.MaxFallSpeed, s.VerticalVelocity - GameRules.Gravity * dt);
                p.Y = s.Position.Y + s.VerticalVelocity * dt;
                if (s.VerticalVelocity > 0)
                {
                    float? ceiling = CeilingAbove(p, s.Position.Y + s.Height, p.Y + s.Height, map);
                    if (ceiling.HasValue)
                    {
                        p.Y = ceiling.Value - s.Height;
                        s.VerticalVelocity = 0;
                    }
                }
            }
            else
            {
                p.Y = s.Position.Y;
            }

            float ground = GroundHeight(p, Math.Max(s.Position.Y, p.Y) + GameRules.StepHeight, map);
            if (s.Grounded)
            {
                if (ground >= p.Y - StepDown) p.Y = ground; // walk up or down stairs
                else s.Grounded = false;                     // walked off a ledge
            }
            else if (p.Y <= ground)
            {
                p.Y = ground;
                s.Grounded = true;
                s.VerticalVelocity = 0;
            }

            s.Position = p;
        }

        /// <summary>
        /// Falling from the plane: steer across the ground with the stick (faster in free fall), fall at a steady speed,
        /// open the parachute with Jump high enough up or automatically near the ground, and land on whatever is below
        /// (ground or roof), ready to walk.
        /// </summary>
        private static void StepDrop(ref MotorState s, InputCommand input, float mx, float mz, float dt, MapLayout map)
        {
            float yaw = WrapYaw(input.Yaw);
            float ground = GroundHeight(s.Position, s.Position.Y, map);
            float height = s.Position.Y - ground;
            if (s.Drop == DropPhase.Freefall &&
                (height <= GameRules.ParachuteAutoHeight || (input.Has(InputButtons.Jump) && height >= GameRules.ParachuteMinHeight)))
                s.Drop = DropPhase.Parachute;

            bool chute = s.Drop == DropPhase.Parachute;
            float speed = chute ? GameRules.ParachuteSpeed : GameRules.FreefallSpeed;
            Vector3 p = s.Position + (Right(yaw) * mx + Forward(yaw) * mz) * speed * dt;
            float limit = map.HalfSize - GameRules.PlayerRadius;
            p.X = Math.Max(-limit, Math.Min(limit, p.X));
            p.Z = Math.Max(-limit, Math.Min(limit, p.Z));
            p = ResolveHorizontal(p, s.Position.Y, GameRules.PlayerHeight, map);
            s.VerticalVelocity = -(chute ? GameRules.ParachuteFallSpeed : GameRules.FreefallFallSpeed);
            p.Y = s.Position.Y + s.VerticalVelocity * dt;
            float below = GroundHeight(p, s.Position.Y, map);
            if (p.Y <= below)
            {
                p.Y = below;
                if (ResolveHorizontal(p, p.Y, GameRules.PlayerHeight, map) != p) p = NearestDry(p, map); // came down in water
                s.Drop = DropPhase.None;
                s.Grounded = true;
                s.VerticalVelocity = 0;
                s.Crouched = false;
            }
            else s.Grounded = false;
            s.Position = p;
        }

        /// <summary>The nearest spot (2 m steps, up to 120 m out) where someone can stand: for landings in water.</summary>
        public static Vector3 NearestDry(Vector3 p, MapLayout map)
        {
            for (float r = 2; r <= 120; r += 2)
            {
                int steps = (int)Math.Max(8, r * 1.5f);
                for (int i = 0; i < steps; i++)
                {
                    double a = i * Math.PI * 2 / steps;
                    var q = new Vector3(p.X + (float)Math.Sin(a) * r, 0, p.Z + (float)Math.Cos(a) * r);
                    if (Math.Abs(q.X) > map.HalfSize - 1 || Math.Abs(q.Z) > map.HalfSize - 1) continue;
                    q.Y = GroundHeight(q, GameRules.StepHeight, map);
                    if (q.Y <= GameRules.StepHeight && ResolveHorizontal(q, q.Y, GameRules.PlayerHeight, map) == q) return q;
                }
            }
            return p;
        }

        /// <summary>One grounded step without jump/crouch state. Kept for simple callers and tests.</summary>
        public static Vector3 Move(Vector3 position, InputCommand input, float dt, MapLayout map)
        {
            MotorState s = MotorState.StandingAt(position);
            Step(ref s, input, dt, map);
            return s.Position;
        }

        /// <summary>Pushes a standing character at ground level out of walls and back inside the map.</summary>
        public static Vector3 ResolveCollisions(Vector3 p, MapLayout map) => ResolveHorizontal(p, p.Y, GameRules.PlayerHeight, map);

        /// <summary>
        /// Horizontal push-out. Boxes the character can step onto (top within <see cref="GameRules.StepHeight"/>
        /// of the feet) and boxes entirely above the head do not block. Water always blocks, from any height: nobody
        /// walks on it or wades in, even after jumping off something next to it. (Shots still pass over it.)
        /// </summary>
        public static Vector3 ResolveHorizontal(Vector3 p, float feetY, float height, MapLayout map)
        {
            float r = GameRules.PlayerRadius;
            float limit = map.HalfSize - r;
            p.X = Math.Max(-limit, Math.Min(limit, p.X));
            p.Z = Math.Max(-limit, Math.Min(limit, p.Z));

            foreach (int i in Near(map, p.X - r, p.Z - r, p.X + r, p.Z + r))
            {
                Aabb box = map.Obstacles[i];
                bool water = map.KindOf(i) == BoxKind.Water;
                if (!water && box.Max.Y <= feetY + GameRules.StepHeight || box.Min.Y >= feetY + height) continue;
                float minX = box.Min.X - r, maxX = box.Max.X + r;
                float minZ = box.Min.Z - r, maxZ = box.Max.Z + r;
                if (p.X <= minX || p.X >= maxX || p.Z <= minZ || p.Z >= maxZ) continue;

                // Push out along the axis of least penetration.
                float left = p.X - minX, right = maxX - p.X, back = p.Z - minZ, front = maxZ - p.Z;
                float min = Math.Min(Math.Min(left, right), Math.Min(back, front));
                if (min == left) p.X = minX;
                else if (min == right) p.X = maxX;
                else if (min == back) p.Z = minZ;
                else p.Z = maxZ;
            }
            return p;
        }

        /// <summary>Highest surface under the footprint whose top is at or below <paramref name="maxTop"/>.</summary>
        public static float GroundHeight(Vector3 p, float maxTop, MapLayout map)
        {
            float ground = 0f;
            float r = GameRules.PlayerRadius * FootInset;
            foreach (int i in Near(map, p.X - r, p.Z - r, p.X + r, p.Z + r))
            {
                Aabb box = map.Obstacles[i];
                if (box.Max.Y > maxTop || box.Max.Y <= ground || map.KindOf(i) == BoxKind.Water) continue; // water is never ground
                if (p.X < box.Min.X - r || p.X > box.Max.X + r || p.Z < box.Min.Z - r || p.Z > box.Max.Z + r) continue;
                ground = box.Max.Y;
            }
            return ground;
        }

        /// <summary>True when a character of <paramref name="height"/> fits at <paramref name="feet"/> (for standing up).</summary>
        public static bool HasHeadroom(Vector3 feet, float height, MapLayout map)
        {
            float r = GameRules.PlayerRadius;
            foreach (int i in Near(map, feet.X - r, feet.Z - r, feet.X + r, feet.Z + r))
            {
                Aabb box = map.Obstacles[i];
                if (box.Min.Y >= feet.Y + height || box.Max.Y <= feet.Y + GameRules.StepHeight) continue;
                if (feet.X <= box.Min.X - r || feet.X >= box.Max.X + r || feet.Z <= box.Min.Z - r || feet.Z >= box.Max.Z + r) continue;
                return false;
            }
            return true;
        }

        private static float? CeilingAbove(Vector3 p, float headFrom, float headTo, MapLayout map)
        {
            float r = GameRules.PlayerRadius * FootInset;
            float? lowest = null;
            foreach (int i in Near(map, p.X - r, p.Z - r, p.X + r, p.Z + r))
            {
                Aabb box = map.Obstacles[i];
                if (box.Min.Y < headFrom - 1e-4f || box.Min.Y > headTo) continue;
                if (p.X < box.Min.X - r || p.X > box.Max.X + r || p.Z < box.Min.Z - r || p.Z > box.Max.Z + r) continue;
                if (!lowest.HasValue || box.Min.Y < lowest.Value) lowest = box.Min.Y;
            }
            return lowest;
        }

        /// <summary>Slab test. Returns distance along the ray to the box, or null when missed within maxDistance.</summary>
        public static float? RayAabb(Vector3 origin, Vector3 dir, Aabb box, float maxDistance)
        {
            float tMin = 0f, tMax = maxDistance;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
                float d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
                float lo = axis == 0 ? box.Min.X : axis == 1 ? box.Min.Y : box.Min.Z;
                float hi = axis == 0 ? box.Max.X : axis == 1 ? box.Max.Y : box.Max.Z;
                if (Math.Abs(d) < 1e-8f)
                {
                    if (o < lo || o > hi) return null;
                    continue;
                }
                float t1 = (lo - o) / d, t2 = (hi - o) / d;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tMin = Math.Max(tMin, t1);
                tMax = Math.Min(tMax, t2);
                if (tMin > tMax) return null;
            }
            return tMin;
        }

        /// <summary>Distance to the nearest wall along the ray, or maxDistance when nothing blocks it.</summary>
        public static float WallDistance(Vector3 origin, Vector3 dir, float maxDistance, MapLayout map) =>
            map.Grid.RayDistance(origin, dir, maxDistance);

        /// <summary>Brute-force reference for <see cref="WallDistance"/> (tests).</summary>
        public static float WallDistanceBruteForce(Vector3 origin, Vector3 dir, float maxDistance, MapLayout map)
        {
            float wall = maxDistance;
            foreach (Aabb box in map.Obstacles)
            {
                float? t = RayAabb(origin, dir, box, maxDistance);
                if (t.HasValue && t.Value < wall) wall = t.Value;
            }
            return wall;
        }

        /// <summary>Ray against a standing character capsule at <paramref name="feet"/>.</summary>
        public static float? RayCapsule(Vector3 origin, Vector3 dir, Vector3 feet, float maxDistance) =>
            RayCapsule(origin, dir, feet, GameRules.PlayerHeight, maxDistance);

        /// <summary>
        /// Ray against a character capsule of the given height. Returns distance along the ray to the closest
        /// approach, or null when the ray passes outside the capsule or beyond maxDistance.
        /// </summary>
        public static float? RayCapsule(Vector3 origin, Vector3 dir, Vector3 feet, float height, float maxDistance) =>
            RayCapsule(origin, dir, feet, height, maxDistance, out _);

        /// <summary>As above; <paramref name="hitHeight"/> is the height above the feet of the closest point on the capsule's spine.</summary>
        public static float? RayCapsule(Vector3 origin, Vector3 dir, Vector3 feet, float height, float maxDistance, out float hitHeight)
        {
            float r = GameRules.PlayerRadius;
            Vector3 a = feet + new Vector3(0, r, 0);
            Vector3 b = feet + new Vector3(0, height - r, 0);
            ClosestPointsSegmentSegment(origin, origin + dir * maxDistance, a, b, out float s, out _, out Vector3 c1, out Vector3 c2);
            hitHeight = c2.Y - feet.Y;
            if (Vector3.DistanceSquared(c1, c2) > r * r) return null;
            return s * maxDistance;
        }

        /// <summary>
        /// Deterministic shot spread: the same (yaw, pitch, spread, seed) gives the same direction on client and
        /// server, so predicted tracers match the server's hit test.
        /// </summary>
        public static Vector3 SpreadDirection(float yaw, float pitch, float spreadDegrees, uint seed)
        {
            uint h = Hash(seed);
            float u = (h & 0xFFFF) / 65536f;
            float v = (h >> 16) / 65536f;
            float radius = spreadDegrees * (float)(Math.PI / 180) * (float)Math.Sqrt(v);
            float angle = u * 2 * (float)Math.PI;
            return AimDirection(yaw + radius * (float)Math.Cos(angle), pitch + radius * (float)Math.Sin(angle));
        }

        public static uint ShotSeed(ushort playerId, uint sequence) => sequence * 2654435761u ^ ((uint)playerId << 20);

        /// <summary>Loot is reachable when within pickup range horizontally and roughly at the character's level.</summary>
        public static bool CanReach(MotorState s, Vector3 item)
        {
            float dx = item.X - s.Position.X, dz = item.Z - s.Position.Z;
            float dy = item.Y - s.Position.Y;
            return dx * dx + dz * dz <= GameRules.PickupRange * GameRules.PickupRange && dy > -1f && dy < 2f;
        }

        private static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }

        private static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        // Ericson, Real-Time Collision Detection, 5.1.9.
        private static void ClosestPointsSegmentSegment(
            Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s, out float t, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float c = Vector3.Dot(d1, r), b = Vector3.Dot(d1, d2);
            float denom = a * e - b * b;
            s = denom > 1e-8f ? Clamp01((b * f - c * e) / denom) : 0f;
            t = (b * s + f) / e;
            if (t < 0f)
            {
                t = 0f;
                s = Clamp01(-c / a);
            }
            else if (t > 1f)
            {
                t = 1f;
                s = Clamp01((b - c) / a);
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }

        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
