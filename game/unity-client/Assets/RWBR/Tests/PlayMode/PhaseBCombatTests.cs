using System.Collections;
using NUnit.Framework;
using RWBR.Client.Combat;
using RWBR.Client.Menu;
using RWBR.Client.Player;
using RWBR.Client.Practice;
using RWBR.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RWBR.Tests
{
    /// <summary>
    /// Phase B acceptance on the training range: move, aim (zoom and scope), fire with effects, reload, and switch
    /// weapons, driven through the same input path as a player. Characters are still placeholder capsules until the
    /// licensed character and animations are imported; these tests cover the weapon, camera and combat systems.
    /// </summary>
    public class PhaseBCombatTests
    {
        private sealed class ScriptedInput : IPlayerInputSource
        {
            public PlayerInputFrame Frame = new PlayerInputFrame { SelectSlot = -1 };

            public PlayerInputFrame Read()
            {
                PlayerInputFrame f = Frame;
                Frame.InteractPressed = Frame.SwitchPressed = Frame.ReloadPressed = Frame.AimToggled = Frame.JumpPressed = false;
                return f;
            }
        }

        [TearDown]
        public void Reset() => PracticeGame.RequestedMap = null;

        private static IEnumerator Wait(float seconds)
        {
            float until = Time.time + seconds;
            while (Time.time < until) yield return null;
        }

        private static IEnumerator LoadRange(ScriptedInput input, System.Action<PracticeGame> got)
        {
            yield return SceneManager.LoadSceneAsync("MainMenu", LoadSceneMode.Single);
            MainMenu.PlayTrainingRange();
            yield return null;
            yield return null;
            PracticeGame game = Object.FindAnyObjectByType<PracticeGame>();
            Assert.IsNotNull(game, "range did not load");
            game.InputOverride = input;
            got(game);
        }

        /// <summary>Face a range target's chest from where the player stands.</summary>
        private static void FaceTarget(PracticeGame game, Fighter target)
        {
            Vector3 eye = game.CameraPivot;
            Vector3 chest = target.Chest.ToUnity();
            Vector3 d = chest - eye;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            game.SetView(yaw, pitch);
        }

        [UnityTest]
        public IEnumerator Aim_fire_and_reload_on_the_range()
        {
            var input = new ScriptedInput();
            PracticeGame game = null;
            yield return LoadRange(input, g => game = g);
            Assert.IsTrue(game.Sim.IsRange);
            Camera cam = Camera.main;
            float hipFov = cam.fieldOfView;

            // The rifle is held as a 3D model with a muzzle.
            yield return null;
            WeaponHolder holder = game.Views.WeaponOf(PracticeSimulation.PlayerId);
            Assert.IsNotNull(holder?.Current, "the player holds a weapon model");
            StringAssert.Contains("AssaultRifle", holder.Current.Root.name);

            // Aim down sights: blend in, narrow the view, send Aim to the simulation.
            Fighter target = game.Sim.Enemies[0]; // 10 m lane
            FaceTarget(game, target);
            input.Frame.Aim = true;
            yield return Wait(0.4f);
            Assert.AreEqual(1f, game.Aim.Blend, 1e-3f);
            Assert.AreEqual(hipFov / game.Sim.PlayerWeapon.AimZoom, cam.fieldOfView, 0.5f);
            Assert.IsFalse(game.Aim.Scoped);

            // Fire a burst at the 10 m target.
            int emitted = game.Views.Effects.Emitted;
            FaceTarget(game, target);
            input.Frame.Fire = true;
            yield return Wait(0.5f);
            input.Frame.Fire = false;
            Assert.Greater(game.ShotsFired, 2);
            Assert.Greater(game.ShotsHit, 0, "aimed shots at 10 m land");
            Assert.Greater(game.Views.Effects.Emitted, emitted, "muzzle flash, shells and impacts were emitted");
            Assert.Greater(game.DamageNumbers.Count + (target.Combat.Alive ? 0 : 1), 0);
            Assert.AreEqual(10f, game.LastHitDistance, 0.5f);

            // Reload: aiming drops while reloading, and the magazine refills.
            input.Frame.ReloadPressed = true;
            yield return Wait(0.3f);
            Assert.IsTrue(game.Sim.Player.Combat.Reloading);
            Assert.IsFalse(game.Aim.Aiming, "no aiming while reloading");
            yield return Wait(game.Sim.PlayerWeapon.ReloadSeconds + 0.3f);
            Assert.IsFalse(game.Sim.Player.Combat.Reloading);
            Assert.AreEqual(game.Sim.PlayerWeapon.MagazineSize, game.Sim.Player.Combat.Ammo);
            Assert.AreEqual(GameRules.MaxReserveAmmo, game.Sim.Player.Combat.ReserveAmmo, "range ammo is endless");
        }

        [UnityTest]
        public IEnumerator Pick_up_switch_and_scope_a_sniper()
        {
            var input = new ScriptedInput();
            PracticeGame game = null;
            yield return LoadRange(input, g => game = g);

            int sniper = -1;
            for (int i = 0; i < game.Sim.Map.Loot.Count; i++)
                if (game.Sim.Map.Loot[i].WeaponIndex == WeaponCatalog.Sniper) sniper = i;
            game.TeleportPlayer(game.Sim.Map.Loot[sniper].Position.ToUnity());
            input.Frame.InteractPressed = true;
            yield return Wait(0.2f);
            Assert.AreEqual(WeaponCatalog.Sniper, game.Sim.Player.Combat.ActiveWeaponIndex);
            yield return null;
            WeaponHolder holder = game.Views.WeaponOf(PracticeSimulation.PlayerId);
            StringAssert.Contains("Sniper", holder.Current.Root.name);

            // Scope in: the camera goes to the eye and the body is hidden.
            input.Frame.Aim = true;
            yield return Wait(0.4f);
            Assert.IsTrue(game.Aim.Scoped);
            Assert.AreEqual(0f, Object.FindAnyObjectByType<ThirdPersonCamera>().CurrentDistance, 0.05f);
            Assert.IsFalse(holder.Current.Root.activeSelf, "the weapon model is hidden in the scope");
            input.Frame.Aim = false;
            yield return Wait(0.3f);
            Assert.IsFalse(game.Aim.Scoped);

            // Switch back to the rifle in the other slot.
            input.Frame.SwitchPressed = true;
            yield return Wait(WeaponCatalog.Get(WeaponCatalog.Rifle).SwitchSeconds + 0.3f);
            Assert.AreEqual(WeaponCatalog.Rifle, game.Sim.Player.Combat.ActiveWeaponIndex);
            StringAssert.Contains("AssaultRifle", holder.Current.Root.name);
        }

        [UnityTest]
        public IEnumerator Aiming_slows_movement()
        {
            var input = new ScriptedInput();
            PracticeGame game = null;
            yield return LoadRange(input, g => game = g);
            game.SetView(90f, 8f); // walk east along the firing line (open ground)
            Vector3 start = game.Sim.Player.Motor.Position.ToUnity();
            input.Frame.Aim = true;
            input.Frame.Move = new Vector2(0, 1);
            yield return Wait(0.5f);
            input.Frame.Move = Vector2.zero;
            float aimed = Vector3.Distance(start, game.Sim.Player.Motor.Position.ToUnity());
            Assert.Less(aimed, GameRules.WalkSpeed * 0.5f * 0.85f, "aimed walking is slower than hip walking");
            Assert.Greater(aimed, 0.5f, "but the player still moves");
        }
    }
}
