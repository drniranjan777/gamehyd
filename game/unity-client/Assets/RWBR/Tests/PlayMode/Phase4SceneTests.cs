using System.Collections;
using NUnit.Framework;
using RWBR.Client.Audio;
using RWBR.Client.Menu;
using RWBR.Client.Player;
using RWBR.Client.Practice;
using RWBR.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RWBR.Tests
{
    public class Phase4SceneTests
    {
        private sealed class ScriptedInput : IPlayerInputSource
        {
            public PlayerInputFrame Frame = new PlayerInputFrame { SelectSlot = -1 };

            public PlayerInputFrame Read()
            {
                PlayerInputFrame f = Frame;
                Frame.InteractPressed = Frame.SwitchPressed = false;
                return f;
            }
        }

        [TearDown]
        public void Reset()
        {
            PracticeGame.RequestedMap = null;
            PracticeGame.TutorialRequested = false;
        }

        private static IEnumerator Wait(float seconds)
        {
            float until = Time.time + seconds;
            while (Time.time < until) yield return null;
        }

        [UnityTest]
        public IEnumerator Main_menu_starts_city_practice_with_enemies_across_the_districts()
        {
            yield return SceneManager.LoadSceneAsync("MainMenu", LoadSceneMode.Single);
            Assert.IsNotNull(Object.FindAnyObjectByType<MainMenu>());
            MainMenu.PlayCityPractice();
            yield return null;
            yield return null;
            PracticeGame game = Object.FindAnyObjectByType<PracticeGame>();
            Assert.IsNotNull(game, "practice scene did not load");
            Assert.IsTrue(game.Sim.IsCity);
            Assert.AreEqual(8, game.Sim.EnemiesAlive);
            Assert.AreEqual(game.Sim.Map.Spawns[0].X, game.Sim.Player.Motor.Position.X, 0.01f);
            Assert.IsNull(game.Tutorial);
        }

        [UnityTest]
        public IEnumerator Tutorial_walks_through_every_step_with_enemies_holding_fire()
        {
            PlayerPrefs.DeleteKey(TutorialDirector.DoneKey);
            PracticeGame.RequestedMap = "greybox-block-100-v2";
            PracticeGame.TutorialRequested = true;
            yield return SceneManager.LoadSceneAsync("Greybox", LoadSceneMode.Single);
            PracticeGame game = Object.FindAnyObjectByType<PracticeGame>();
            var input = new ScriptedInput();
            game.InputOverride = input;
            TutorialDirector t = game.Tutorial;
            Assert.IsNotNull(t);
            Assert.IsTrue(game.Sim.EnemiesPassive);

            // 1. Look around.
            input.Frame.Look = new Vector2(8, 0);
            yield return Wait(0.6f);
            input.Frame.Look = Vector2.zero;
            game.SetView(0, 8);
            Assert.AreEqual(1, t.Index, "look step");

            // 2-3. Walk north, through the doorway.
            input.Frame.Move = Vector2.up;
            yield return Wait(4.2f);
            input.Frame.Move = Vector2.zero;
            yield return null;
            Assert.AreEqual(3, t.Index, $"walk/enter steps (player at {game.Sim.Player.Motor.Position})");

            // 4. Pick up the rifle.
            LootSpawn rifle = game.Sim.Map.Loot[0];
            game.TeleportPlayer(new Vector3(rifle.Position.X + 1, 0, rifle.Position.Z));
            input.Frame.InteractPressed = true;
            yield return Wait(0.2f);
            Assert.AreEqual(4, t.Index, "pickup step");

            // 5. Shoot the enemy inside the building (it must not shoot back).
            Fighter enemy = game.Sim.Enemies[1];
            System.Numerics.Vector3 d = enemy.Chest - game.Sim.Player.Motor.Eye;
            game.SetView(Mathf.Atan2(d.X, d.Z) * Mathf.Rad2Deg, 2);
            input.Frame.Fire = true;
            yield return Wait(1.0f);
            input.Frame.Fire = false;
            Assert.AreEqual(5, t.Index, "shoot step");
            Assert.AreEqual(GameRules.MaxHealth, game.Sim.Player.Combat.Health, "enemies fired during training");
            Assert.Greater(GameAudio.Instance.PlayedCount, 0, "no sounds were played");

            // 6. Climb the outside stairs to the roof.
            game.TeleportPlayer(new Vector3(8.75f, 0, -9f));
            game.SetView(0, 8);
            input.Frame.Move = Vector2.up;
            yield return Wait(2.6f);
            input.Frame.Move = Vector2.zero;
            yield return null;
            Assert.IsTrue(t.Finished, $"roof step (y = {game.Sim.Player.Motor.Position.Y})");
            Assert.IsFalse(game.Sim.EnemiesPassive, "enemies should fight after training");
            Assert.IsTrue(TutorialDirector.Done);
        }
    }
}
