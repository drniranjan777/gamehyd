using System;
using System.Numerics;

namespace RWBR.Shared
{
    /// <summary>Deterministic movement and hit geometry, shared by server authority and client prediction.</summary>
    public static class Simulation
    {
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
        /// Advances a character by one tick. Oversized or non-finite input is clamped, so a tampered client
        /// can never move faster than <see cref="GameRules.SprintSpeed"/>.
        /// </summary>
        public static Vector3 Move(Vector3 position, InputCommand input, float dt, MapLayout map)
        {
            float mx = float.IsNaN(input.MoveX) || float.IsInfinity(input.MoveX) ? 0 : input.MoveX;
            float mz = float.IsNaN(input.MoveZ) || float.IsInfinity(input.MoveZ) ? 0 : input.MoveZ;
            float len = (float)Math.Sqrt(mx * mx + mz * mz);
            if (len > 1f)
            {
                mx /= len;
                mz /= len;
            }

            float yaw = WrapYaw(input.Yaw);
            bool sprint = input.Has(InputButtons.Sprint) && mz > 0.5f;
            float speed = sprint ? GameRules.SprintSpeed : GameRules.WalkSpeed;
            Vector3 delta = (Right(yaw) * mx + Forward(yaw) * mz) * speed * dt;
            return ResolveCollisions(position + delta, map);
        }

        public static Vector3 ResolveCollisions(Vector3 p, MapLayout map)
        {
            float r = GameRules.PlayerRadius;
            float limit = map.HalfSize - r;
            p.X = Math.Max(-limit, Math.Min(limit, p.X));
            p.Z = Math.Max(-limit, Math.Min(limit, p.Z));
            p.Y = 0;

            foreach (Aabb box in map.Obstacles)
            {
                if (box.Min.Y >= GameRules.PlayerHeight) continue; // overhead (roofs, slabs): no ground collision
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

        /// <summary>
        /// Ray against a standing character capsule at <paramref name="feet"/>. Returns distance along the ray
        /// to the closest approach, or null when the ray passes outside the capsule or beyond maxDistance.
        /// </summary>
        public static float? RayCapsule(Vector3 origin, Vector3 dir, Vector3 feet, float maxDistance)
        {
            float r = GameRules.PlayerRadius;
            Vector3 a = feet + new Vector3(0, r, 0);
            Vector3 b = feet + new Vector3(0, GameRules.PlayerHeight - r, 0);
            ClosestPointsSegmentSegment(origin, origin + dir * maxDistance, a, b, out float s, out _, out Vector3 c1, out Vector3 c2);
            if (Vector3.DistanceSquared(c1, c2) > r * r) return null;
            return s * maxDistance;
        }

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
