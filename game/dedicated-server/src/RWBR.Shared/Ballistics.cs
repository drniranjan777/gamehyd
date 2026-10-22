using System.Collections.Generic;
using System.Numerics;

namespace RWBR.Shared
{
    /// <summary>A character as a shot sees it (lag-compensated position on the server).</summary>
    public struct TargetCapsule
    {
        public Vector3 Feet;
        public float Height;
    }

    public struct PelletResult
    {
        /// <summary>Index into the targets list, or -1 for a miss / wall.</summary>
        public int Target;
        public float Distance;
        public bool Headshot;
        /// <summary>Damage before armor (falloff and headshot applied).</summary>
        public float Damage;
        public Vector3 End;
    }

    /// <summary>
    /// One trigger pull, shared by the server and the offline client: deterministic spread per pellet, walls block,
    /// nearest capsule hit, distance falloff, headshot multiplier (BRD 5.2: server-authoritative hit validation).
    /// </summary>
    public static class Ballistics
    {
        /// <summary>
        /// A ray whose closest approach is at or above the top of the capsule's spine (height - radius) passes
        /// through the head: the top 0.4 m standing, 0.4 m of a crouched capsule too.
        /// </summary>
        public static bool IsHeadshot(float hitHeight, float capsuleHeight) => hitHeight >= capsuleHeight - GameRules.PlayerRadius - 0.01f;

        public static void Fire(Vector3 origin, float yaw, float pitch, float spreadDegrees, uint seed, WeaponDefinition weapon,
            MapLayout map, IReadOnlyList<TargetCapsule> targets, List<PelletResult> results)
        {
            results.Clear();
            int pellets = weapon.Pellets < 1 ? 1 : weapon.Pellets;
            for (int p = 0; p < pellets; p++)
            {
                uint pelletSeed = p == 0 ? seed : seed + (uint)p * 0x9E3779B9u;
                Vector3 dir = Simulation.SpreadDirection(yaw, pitch, spreadDegrees, pelletSeed);
                float best = Simulation.WallDistance(origin, dir, weapon.Range, map);
                int hit = -1;
                bool head = false;
                for (int i = 0; i < targets.Count; i++)
                {
                    TargetCapsule t = targets[i];
                    float? d = Simulation.RayCapsule(origin, dir, t.Feet, t.Height, weapon.Range, out float hitHeight);
                    if (!d.HasValue || d.Value >= best) continue;
                    best = d.Value;
                    hit = i;
                    head = IsHeadshot(hitHeight, t.Height);
                }
                results.Add(new PelletResult
                {
                    Target = hit,
                    Distance = best,
                    Headshot = hit >= 0 && head,
                    Damage = hit < 0 ? 0 : weapon.DamageAt(best) * (head ? weapon.HeadshotMultiplier : 1f),
                    End = origin + dir * best,
                });
            }
        }
    }
}
