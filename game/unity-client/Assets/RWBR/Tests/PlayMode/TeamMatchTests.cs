using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RWBR.Client.Online;
using RWBR.Shared;
using RWBR.Shared.Server;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RWBR.Tests
{
    /// <summary>Phase C part 6a in the game: Squad bot practice puts three bots on your team.</summary>
    public class TeamMatchTests
    {
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
        public IEnumerator Squad_bot_practice_gives_you_three_bot_teammates()
        {
            MatchConfig cfg = LocalMatch.BotMatchConfig(MapLayout.GreyboxBlock(100f), 7);
            cfg.TeamSize = 4;
            cfg.CountdownSeconds = 1f;
            cfg.ZoneStages = new List<ZoneStage> { new ZoneStage(600, 1, 50) };
            OnlineGame.RequestedBotMatch = 7;
            OnlineGame.RequestedBotConfig = cfg;
            yield return SceneManager.LoadSceneAsync("Online", LoadSceneMode.Single);
            var game = UnityEngine.Object.FindAnyObjectByType<OnlineGame>();
            yield return WaitUntil(() => game.Net.Latest != null && game.Net.Latest.Phase == RWBR.Shared.Protocol.MatchPhase.Active, 15f, "the match to start");
            Assert.AreEqual(4, game.Net.Welcome.TeamSize);
            Assert.AreEqual(3, game.Net.Latest.SelfTeammates.Count);
            PlayerState me = game.Local.Match.Players.First(p => p.Id == game.Net.PlayerId);
            foreach (ushort id in game.Net.Latest.SelfTeammates)
                Assert.AreEqual(me.Team, game.Local.Match.Players.First(p => p.Id == id).Team);
            game.LeaveToMenu();
            yield return WaitUntil(() => SceneManager.GetActiveScene().name == "MainMenu", 10f, "the main menu");
        }
    }
}
