using NUnit.Framework;
using RWBR.Client.Player;
using RWBR.Client.Practice;
using RWBR.Shared;
using UnityEngine;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Tests
{
    /// <summary>Phase B: aim blend, zoom, recoil recovery, and the training range simulation.</summary>
    public class AimAndRangeTests
    {
        [Test]
        public void Aim_blends_in_over_the_blend_time_and_zooms_by_the_weapon()
        {
            var aim = new AimState();
            aim.Update(true, 1.5f, AimState.BlendSeconds / 2);
            Assert.AreEqual(0.5f, aim.Blend, 0.01f);
            Assert.IsTrue(aim.FovDivisor > 1f && aim.FovDivisor < 1.5f);
            aim.Update(true, 1.5f, AimState.BlendSeconds);
            Assert.AreEqual(1f, aim.Blend);
            Assert.AreEqual(1.5f, aim.FovDivisor, 1e-4f);
            Assert.IsFalse(aim.Scoped, "1.5x is not a scope");
            aim.Update(false, 1.5f, AimState.BlendSeconds);
            Assert.AreEqual(0f, aim.Blend);
        }

        [Test]
        public void Scoped_weapons_turn_slower_in_proportion_to_zoom()
        {
            var aim = new AimState();
            aim.Update(true, 6f, 1f);
            Assert.IsTrue(aim.Scoped);
            Assert.AreEqual(1f / 6f, aim.SensitivityScale(1f), 1e-4f);
            Assert.AreEqual(2f / 6f, aim.SensitivityScale(2f), 1e-4f);
            aim.Update(false, 6f, 1f);
            Assert.AreEqual(1f, aim.SensitivityScale(1f), 1e-4f, "hip fire is unaffected");
        }

        [Test]
        public void Recoil_kick_recovers_but_the_climb_stays()
        {
            var aim = new AimState();
            aim.Update(false, 1.5f, 0.016f);
            Vector2 climb = aim.Recoil(2f, 0.5f);
            Assert.AreEqual(-2f * AimState.ClimbShare, climb.x, 1e-4f, "climb pulls the view up (negative pitch)");
            Assert.AreEqual(-2f * AimState.KickShare, aim.KickPitch, 1e-4f);
            for (int i = 0; i < 60; i++) aim.Update(false, 1.5f, 1 / 60f);
            Assert.AreEqual(0f, aim.KickPitch, 0.01f, "the kick has recovered after a second");

            var aimed = new AimState();
            aimed.Update(true, 1.5f, 1f);
            Assert.Less(Mathf.Abs(aimed.Recoil(2f, 0.5f).x), Mathf.Abs(climb.x), "aimed recoil is smaller");
        }

        [Test]
        public void Touch_buttons_including_aim_do_not_overlap_on_phone_screens()
        {
            bool hasAim = false;
            foreach (float aspect in new[] { 16f / 9f, 20f / 9f })
            {
                TouchControls.ButtonLayout[] layout = TouchControls.Layout;
                for (int i = 0; i < layout.Length; i++)
                {
                    hasAim |= layout[i].Button == TouchButton.Aim;
                    for (int j = i + 1; j < layout.Length; j++)
                    {
                        // Centres are screen fractions; radii are fractions of the screen height.
                        var a = new Vector2(layout[i].Center.x * aspect, layout[i].Center.y);
                        var b = new Vector2(layout[j].Center.x * aspect, layout[j].Center.y);
                        Assert.GreaterOrEqual(Vector2.Distance(a, b), layout[i].Radius + layout[j].Radius,
                            $"{layout[i].Label} overlaps {layout[j].Label} at {aspect:0.00}:1");
                    }
                }
            }
            Assert.IsTrue(hasAim);
        }

        private static PracticeSimulation Range() => new PracticeSimulation(MapLayout.TrainingRange());

        [Test]
        public void Range_starts_armed_with_passive_targets_at_every_distance()
        {
            PracticeSimulation sim = Range();
            Assert.IsTrue(sim.IsRange);
            Assert.IsTrue(sim.Player.Combat.HasWeapon);
            Assert.AreEqual(GameRules.MaxReserveAmmo, sim.Player.Combat.ReserveAmmo);
            Assert.AreEqual(MapLayout.RangeDistances.Length + 2, sim.Enemies.Count, "a static target per lane and two moving targets");
            for (int lane = 0; lane < MapLayout.RangeDistances.Length; lane++)
                Assert.AreEqual(MapLayout.RangeDistances[lane], sim.TargetDistance(sim.Enemies[lane]), 0.01f);
            foreach (Fighter t in sim.Enemies) Assert.IsFalse(t.Combat.HasWeapon, "targets are unarmed");

            int weapons = 0;
            foreach (LootSpawn l in sim.Map.Loot)
                if (l.WeaponIndex >= 0) weapons++;
            Assert.AreEqual(WeaponCatalog.Count, weapons, "every weapon is on the bench");
        }

        [Test]
        public void Downed_targets_stand_up_again_and_moving_targets_move()
        {
            PracticeSimulation sim = Range();
            Fighter target = sim.Enemies[0];
            Fighter mover = sim.Enemies[sim.Enemies.Count - 1];
            SVector3 moverStart = mover.Motor.Position;
            target.Combat.ApplyDamage(1000);
            Assert.IsFalse(target.Combat.Alive);

            int ticks = (int)(PracticeSimulation.TargetRespawnSeconds * PracticeSimulation.TickRate) + 2;
            for (int i = 0; i < ticks; i++) sim.Step(new InputCommand { Sequence = (uint)i + 1 });

            Assert.IsTrue(target.Combat.Alive, "target stood up again");
            Assert.AreEqual(GameRules.MaxHealth, target.Combat.Health);
            Assert.AreEqual(PracticeOutcome.InProgress, sim.Outcome, "the range never ends");
            Assert.Greater(SVector3.Distance(moverStart, mover.Motor.Position), 1f);
        }

        [Test]
        public void Range_weapon_rack_never_empties_and_ammo_is_endless()
        {
            PracticeSimulation sim = Range();
            int pistol = -1;
            for (int i = 0; i < sim.Map.Loot.Count; i++)
                if (sim.Map.Loot[i].WeaponIndex == WeaponCatalog.Pistol) pistol = i;
            sim.Player.Motor = MotorState.StandingAt(sim.Map.Loot[pistol].Position);
            sim.Player.Combat.ReserveAmmo = 3;
            sim.Step(new InputCommand { Sequence = 1, Buttons = InputButtons.Interact });

            Assert.AreEqual(WeaponCatalog.Pistol, sim.Player.Combat.ActiveWeaponIndex);
            Assert.IsFalse(sim.LootTaken[pistol]);
            Assert.AreEqual(GameRules.MaxReserveAmmo, sim.Player.Combat.ReserveAmmo);
        }

        [Test]
        public void Practice_spread_tightens_while_aiming()
        {
            PracticeSimulation sim = Range();
            WeaponDefinition rifle = sim.PlayerWeapon;
            float hip = sim.CurrentSpread(sim.Player, new InputCommand(), rifle);
            float aimed = sim.CurrentSpread(sim.Player, new InputCommand { Buttons = InputButtons.Aim }, rifle);
            Assert.AreEqual(hip * rifle.AimSpreadMultiplier, aimed, 1e-4f);
        }
    }
}
