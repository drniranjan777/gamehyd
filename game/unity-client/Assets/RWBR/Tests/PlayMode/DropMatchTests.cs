using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using RWBR.Client.Online;
using RWBR.Client.Player;
using RWBR.Shared;
using RWBR.Shared.Server;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RWBR.Tests
{
    /// <summary>
    /// Phase C part 6b in the game: an offline city match starts in the plane. You see the plane and ride it, jump with
    /// Jump, fall, come down under a parachute and land ready to play.
    /// </summary>
    public class DropMatchTests
    {
        private sealed class ScriptedInput : IPlayerInputSource
        {
            public PlayerInputFrame Frame;

            public PlayerInputFrame Read()
            {
                PlayerInputFrame f = Frame;
                Frame.JumpPressed = false; // a press lasts one frame
                return f;
            }
        }

        private static IEnumerator WaitUntil(Func<bool> condition, float seconds, string what)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > until) Assert.Fail($"timed out waiting for {what}");
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Ride_the_plane_jump_open_the_parachute_and_land()
        {
            MatchConfig cfg = LocalMatch.BotMatchConfig(CityMap.Build(), 2);
            Assert.IsTrue(cfg.PlaneDrop);
            cfg.CountdownSeconds = 1f;
            OnlineGame.RequestedBotMatch = 2;
            OnlineGame.RequestedBotConfig = cfg;
            yield return SceneManager.LoadSceneAsync("Online", LoadSceneMode.Single);
            var game = UnityEngine.Object.FindAnyObjectByType<OnlineGame>();
            var input = new ScriptedInput();
            game.InputOverride = input;
            yield return WaitUntil(() => game.InPlane, 15f, "boarding the plane");
            yield return WaitUntil(() => game.Views.PlaneShown, 5f, "the plane to be drawn");
            Assert.Greater(game.Views.SelfTransform.position.y, GameRules.PlaneAltitude - 5, "you ride the plane");

            input.Frame.JumpPressed = true;
            yield return WaitUntil(() => !game.InPlane && game.Dropping, 5f, "jumping out");
            bool sawParachute = false;
            yield return WaitUntil(() =>
            {
                sawParachute |= game.Views.ParachutesShown > 0;
                return !game.Dropping;
            }, 20f, "landing");
            Assert.IsTrue(sawParachute, "a parachute opened on the way down");
            Assert.Less(game.Views.SelfTransform.position.y, 4f, "on the ground (or a roof)");

            game.LeaveToMenu();
            yield return WaitUntil(() => SceneManager.GetActiveScene().name == "MainMenu", 10f, "the main menu");
        }
    }
}
