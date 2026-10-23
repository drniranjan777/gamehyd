using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using RWBR.Client.Online;
using RWBR.Client.Player;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using RWBR.Shared.Server;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RWBR.Tests
{
    /// <summary>
    /// Phase C part 1, end to end in the game: the offline bot match runs the server's match rules inside the client.
    /// The player joins, moves with prediction, pauses (which stops the match), and plays on until the result screen,
    /// then returns to the main menu. A small map and a fast zone keep it short.
    /// </summary>
    public class BotMatchTests
    {
        private sealed class ScriptedInput : IPlayerInputSource
        {
            public PlayerInputFrame Frame;
            public PlayerInputFrame Read() => Frame;
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

        private static MatchConfig FastConfig(int bots)
        {
            MatchConfig cfg = LocalMatch.BotMatchConfig(MapLayout.GreyboxBlock(40f), bots);
            cfg.CountdownSeconds = 1f;
            cfg.ZoneDamagePerSecond = 50f;
            cfg.ZoneStages = new List<ZoneStage> { new ZoneStage(1, 4, 10), new ZoneStage(1, 3, 0) };
            cfg.MaxMatchSeconds = 40f;
            return cfg;
        }

        [UnityTest]
        public IEnumerator Bot_match_runs_offline_from_join_to_results_and_back_to_the_menu()
        {
            const int bots = 3;
            OnlineGame.RequestedBotMatch = bots;
            OnlineGame.RequestedBotConfig = FastConfig(bots);
            yield return SceneManager.LoadSceneAsync("Online", LoadSceneMode.Single);
            var game = UnityEngine.Object.FindAnyObjectByType<OnlineGame>();
            Assert.IsNotNull(game);
            var input = new ScriptedInput();
            game.InputOverride = input;

            yield return WaitUntil(() => game.Net.State == ConnectionState.Connected && game.Net.Latest != null, 10f, "joining the local match");
            Assert.IsNotNull(game.Local, "an offline bot match is hosted in the game");
            Assert.IsTrue(game.Net.IsLocal);
            Assert.AreEqual(OnlineGame.RequestedBotMatch, 0, "the request is used once");
            Assert.AreEqual(bots + 1, game.Local.Match.Players.Count);
            Assert.AreEqual("this device", game.Net.ServerAddress);

            yield return WaitUntil(() => game.Net.Latest.Phase == MatchPhase.Active, 10f, "the countdown to end");
            Assert.IsFalse(game.Local.Match.Config.StartWithWeapon);

            // Pausing offline stops the match.
            game.Paused = true;
            yield return null;
            long frozen = game.Local.Match.Tick;
            float until = Time.realtimeSinceStartup + 0.5f;
            while (Time.realtimeSinceStartup < until) yield return null;
            Assert.AreEqual(frozen, game.Local.Match.Tick, "paused: no ticks");
            game.Paused = false;

            // Walk forward: the server (in the game) moves the player.
            SnapshotEntry start = game.Self.Value;
            input.Frame = new PlayerInputFrame { Move = Vector2.up };
            until = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < until) yield return null;
            input.Frame = default;
            SnapshotEntry moved = game.Self.Value;
            float walked = new Vector2(moved.X - start.X, moved.Z - start.Z).magnitude;
            Assert.Greater(walked, 1f, "the player moved in the authoritative match");

            yield return WaitUntil(() => game.Net.Result != null, 60f, "the match result");
            MatchCompletedPacket result = game.Net.Result;
            Assert.AreEqual(bots + 1, result.Placements.Count);
            Assert.That(result.Placements, Has.Some.Matches<PlacementEntry>(p => p.PlayerId == game.Net.PlayerId));
            Debug.Log($"RWBR bot match test: {result.Reason}, winner {result.WinnerId}, {game.Local.Match.Players.Count} players, " +
                      $"{game.Local.Match.Stats.Pickups} pickups, {game.Local.Match.Tick} ticks");

            game.LeaveToMenu();
            yield return WaitUntil(() => SceneManager.GetActiveScene().name == "MainMenu", 10f, "the main menu");
        }
    }
}
