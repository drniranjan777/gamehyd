using System.Collections;
using NUnit.Framework;
using RWBR.Client.Player;
using RWBR.Client.Practice;
using RWBR.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RWBR.Tests
{
    public class GreyboxSceneTests
    {
        private sealed class ScriptedInput : IPlayerInputSource
        {
            public PlayerInputFrame Frame;

            public PlayerInputFrame Read()
            {
                PlayerInputFrame f = Frame;
                // Presses last one frame, like real input.
                Frame.InteractPressed = Frame.JumpPressed = Frame.ReloadPressed = Frame.HealPressed = false;
                return f;
            }
        }

        private static IEnumerator LoadPractice(System.Action<PracticeGame, ScriptedInput> ready)
        {
            yield return SceneManager.LoadSceneAsync("Greybox", LoadSceneMode.Single);
            PracticeGame game = Object.FindAnyObjectByType<PracticeGame>();
            Assert.IsNotNull(game, "scene has no PracticeGame");
            var input = new ScriptedInput();
            game.InputOverride = input;
            ready(game, input);
        }

        private static IEnumerator Wait(float seconds)
        {
            float until = Time.time + seconds;
            while (Time.time < until) yield return null;
        }

        [UnityTest]
        public IEnumerator Player_walks_through_the_doorway_into_the_building()
        {
            PracticeGame game = null;
            ScriptedInput input = null;
            yield return LoadPractice((g, i) => { game = g; input = i; });

            // Spawn is at z=-25 facing north; 4 s of walking reaches the room behind the south door (z=-6).
            input.Frame = new PlayerInputFrame { Move = Vector2.up };
            yield return Wait(4.2f);
            input.Frame = default;
            yield return null;

            Vector3 p = game.Views.PlayerTransform.position;
            Assert.That(p.z, Is.InRange(-6f, 6f), $"player is not inside the building: {p}");

            Camera cam = Camera.main;
            Assert.IsNotNull(cam);
            Assert.Less(Vector3.Distance(cam.transform.position, p), 6f, "camera is not following the player");
        }

        [UnityTest]
        public IEnumerator Picking_up_the_rifle_and_firing_spends_ammo_and_draws_tracers()
        {
            PracticeGame game = null;
            ScriptedInput input = null;
            yield return LoadPractice((g, i) => { game = g; input = i; });

            LootSpawn rifle = game.Sim.Map.Loot[0];
            Assert.AreEqual(LootType.Rifle, rifle.Type);
            game.TeleportPlayer(new Vector3(rifle.Position.X + 1, 0, rifle.Position.Z));
            input.Frame = new PlayerInputFrame { InteractPressed = true };
            yield return Wait(0.2f);
            Assert.IsTrue(game.Sim.Player.Combat.HasWeapon, "rifle was not picked up");

            int before = game.Sim.Player.Combat.Ammo;
            int tracersBefore = game.Views.TracersCreated;
            input.Frame = new PlayerInputFrame { Fire = true };
            yield return Wait(0.5f);
            input.Frame = default;
            Assert.Less(game.Sim.Player.Combat.Ammo, before, "firing did not use ammo");
            Assert.Greater(game.Views.TracersCreated, tracersBefore, "no tracer was drawn");
        }

        [UnityTest]
        public IEnumerator Restart_resets_the_run()
        {
            PracticeGame game = null;
            yield return LoadPractice((g, i) => game = g);
            game.Sim.Player.Combat.Health = 10;
            game.Restart();
            yield return null;
            Assert.AreEqual(GameRules.MaxHealth, game.Sim.Player.Combat.Health);
            Assert.AreEqual(5, game.Sim.EnemiesAlive);
        }
    }
}
