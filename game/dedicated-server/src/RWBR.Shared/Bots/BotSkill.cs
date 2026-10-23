#nullable enable
using System;
using System.Numerics;

namespace RWBR.Shared.Bots
{
    public enum BotDifficulty : byte
    {
        Easy = 0,
        Normal = 1,
        Hard = 2,
    }

    /// <summary>
    /// How well a bot plays. Easy bots react late, miss more, fight only up close and never use grenades; hard bots react
    /// quickly, aim tightly, fight at range and use frags and smoke.
    /// </summary>
    public sealed class BotSkill
    {
        /// <summary>Seconds between first seeing someone and opening fire.</summary>
        public float ReactionSeconds { get; set; }
        /// <summary>Random aim error (radians, either way) on each shot decision.</summary>
        public float AimError { get; set; }
        /// <summary>Beyond this range a bot doesn't shoot (it closes in or ignores).</summary>
        public float EngageRange { get; set; }
        public bool UsesGrenades { get; set; }
        /// <summary>Picks the better of its two weapons for the range.</summary>
        public bool ChoosesWeapons { get; set; }
        /// <summary>Health below which it heals when nobody is in sight.</summary>
        public float HealBelow { get; set; }

        public static BotSkill For(BotDifficulty d)
        {
            switch (d)
            {
                case BotDifficulty.Easy:
                    return new BotSkill { ReactionSeconds = 0.9f, AimError = 0.07f, EngageRange = 30f, UsesGrenades = false, ChoosesWeapons = false, HealBelow = 50f };
                case BotDifficulty.Hard:
                    return new BotSkill { ReactionSeconds = 0.2f, AimError = 0.012f, EngageRange = 70f, UsesGrenades = true, ChoosesWeapons = true, HealBelow = 80f };
                default:
                    return new BotSkill { ReactionSeconds = 0.45f, AimError = 0.03f, EngageRange = 45f, UsesGrenades = true, ChoosesWeapons = true, HealBelow = 70f };
            }
        }

        /// <summary>
        /// How well a weapon class suits a distance (higher is better): shotguns and SMGs close, rifles in the middle,
        /// marksman rifles and snipers far.
        /// </summary>
        public static float Suitability(WeaponClass c, float distance)
        {
            switch (c)
            {
                case WeaponClass.Shotgun: return distance < 10 ? 10 : distance < 20 ? 4 : 0;
                case WeaponClass.Smg: return distance < 20 ? 8 : distance < 40 ? 5 : 1;
                case WeaponClass.Pistol: return distance < 25 ? 4 : 1;
                case WeaponClass.AssaultRifle: return distance < 10 ? 5 : distance < 80 ? 7 : 4;
                case WeaponClass.Marksman: return distance < 25 ? 3 : distance < 150 ? 8 : 7;
                case WeaponClass.Sniper: return distance < 30 ? 2 : 9;
                default: return 0;
            }
        }

        private static float[]? _throwPitches;

        /// <summary>
        /// The pitch (radians, up positive) that lands a grenade thrown at standing eye height about
        /// <paramref name="distance"/> metres away on flat ground, using the same throw speed and gravity as the
        /// server. Grenades reach about 25 m at most.
        /// </summary>
        public static float ThrowPitch(float distance)
        {
            float[] table = _throwPitches ??= BuildThrowTable();
            int i = (int)Math.Round(Math.Clamp(distance, 0, table.Length - 1));
            return table[i];
        }

        private static float[] BuildThrowTable()
        {
            var table = new float[26];
            for (int d = 0; d < table.Length; d++)
            {
                float bestPitch = -0.5f, bestError = float.MaxValue;
                for (float pitch = -0.6f; pitch <= 0.8f; pitch += 0.01f)
                {
                    float range = FlatRange(pitch);
                    float err = Math.Abs(range - d);
                    if (err < bestError)
                    {
                        bestError = err;
                        bestPitch = pitch;
                    }
                }
                table[d] = bestPitch;
            }
            return table;
        }

        private static float FlatRange(float pitch)
        {
            Vector3 dir = Simulation.AimDirection(0, pitch);
            Vector3 v = dir * Server.Match.GrenadeSpeed + new Vector3(0, Server.Match.GrenadeLift, 0);
            Vector3 p = new Vector3(0, GameRules.EyeHeight, 0) + new Vector3(dir.X, 0, dir.Z) * 0.4f;
            const float dt = 1f / 30;
            for (int i = 0; i < 300; i++)
            {
                v.Y -= GameRules.Gravity * dt;
                p += v * dt;
                if (p.Y <= 0) break;
            }
            return new Vector2(p.X, p.Z).Length();
        }
    }
}
