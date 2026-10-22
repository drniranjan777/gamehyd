using System.Linq;
using NUnit.Framework;
using RWBR.Client.Audio;
using RWBR.Shared;
using UnityEngine;

namespace RWBR.Tests
{
    public class AudioTests
    {
        [Test]
        public void Every_weapon_class_has_its_own_audible_unclipped_gunshot()
        {
            var clips = System.Enum.GetValues(typeof(WeaponClass)).Cast<WeaponClass>().Select(GameAudio.GunshotClip).ToList();
            foreach (AudioClip c in clips)
            {
                float peak = GameAudio.Peak(c);
                Assert.That(peak, Is.InRange(0.05f, 1f), c.name);
            }
            Assert.AreEqual(clips.Count, clips.Select(c => c.name).Distinct().Count());
            Assert.Greater(GameAudio.GunshotClip(WeaponClass.Sniper).length, GameAudio.GunshotClip(WeaponClass.Smg).length);
        }

        [Test]
        public void Interface_and_world_cues_are_audible()
        {
            foreach (Sfx s in System.Enum.GetValues(typeof(Sfx)))
                Assert.That(GameAudio.Peak(GameAudio.Clip(s)), Is.InRange(0.05f, 1f), s.ToString());
        }

        [Test]
        public void Footsteps_follow_distance_walked_and_crouching_is_quieter()
        {
            var walk = new FootstepTracker();
            var crouch = new FootstepTracker();
            int walkSteps = 0, crouchSteps = 0;
            for (int i = 1; i <= 300; i++) // 30 m in 10 cm increments
            {
                if (walk.Step(1, new Vector3(0, 0, i * 0.1f), grounded: true, crouched: false)) walkSteps++;
                if (crouch.Step(1, new Vector3(0, 0, i * 0.1f), grounded: true, crouched: true)) crouchSteps++;
            }
            Assert.That(walkSteps, Is.InRange(13, 14));  // 30 m / 2.2 m stride
            Assert.That(crouchSteps, Is.InRange(9, 10)); // 30 m / 3.0 m stride
            Assert.Less(crouchSteps, walkSteps);
            var air = new FootstepTracker();
            Assert.IsFalse(Enumerable.Range(1, 100).Any(i => air.Step(2, new Vector3(0, 0, i), grounded: false, crouched: false)));
        }
    }
}
