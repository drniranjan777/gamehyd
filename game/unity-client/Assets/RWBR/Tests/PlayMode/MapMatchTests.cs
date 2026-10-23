using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using RWBR.Client.Audio;
using RWBR.Client.Online;
using RWBR.Shared;
using RWBR.Shared.Server;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RWBR.Tests
{
    /// <summary>
    /// Phase C part 3 in the game: an offline match on hyd-city-v2 with a moving zone. The client's loot spots are the
    /// match's (loot table from Welcome), the next circle arrives, a marker can be placed and cleared, the map screen
    /// opens, and the district's ambience plays.
    /// </summary>
    public class MapMatchTests
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
        public IEnumerator Map_v2_match_has_the_loot_table_a_moving_zone_and_markers()
        {
            MatchConfig cfg = LocalMatch.BotMatchConfig(CityMap.BuildV2(), 2);
            cfg.CountdownSeconds = 1f;
            cfg.ZoneSeed = 77;
            cfg.LootTable = new LootTableConfig { Version = 5, FragPercent = 30 };
            cfg.ZoneStages = new List<ZoneStage> { new ZoneStage(600, 60, 150), new ZoneStage(60, 60, 0) };
            OnlineGame.RequestedBotMatch = 2;
            OnlineGame.RequestedBotConfig = cfg;
            yield return SceneManager.LoadSceneAsync("Online", LoadSceneMode.Single);
            var game = UnityEngine.Object.FindAnyObjectByType<OnlineGame>();
            yield return WaitUntil(() => game.Net.Latest != null && game.Net.Latest.Phase == RWBR.Shared.Protocol.MatchPhase.Active, 30f, "the match to start");

            Assert.AreEqual(CityMap.VersionV2, game.Map.Version);
            Assert.AreEqual(5, game.Net.Welcome.LootTable.Version);
            IReadOnlyList<LootSpawn> server = game.Local.Match.Loot;
            Assert.AreEqual(server.Count, game.Loot.Count);
            for (int i = 0; i < server.Count; i++) Assert.AreEqual(server[i].Kind, game.Loot[i].Kind, $"spot {i}");

            var s = game.Net.Latest;
            Assert.Less(s.NextZoneRadius, s.ZoneRadius, "the next circle is announced");
            Assert.AreNotEqual(0f, s.NextZoneX + s.NextZoneZ, "and it is off-centre (seed 77)");

            game.SetMarker(new Vector2(100, -50));
            Assert.IsTrue(game.Views.MarkerShown);
            Assert.AreEqual(new Vector2(100, -50), game.Marker);
            game.SetMarker(new Vector2(105, -48)); // next to it: clears
            Assert.IsNull(game.Marker);
            Assert.IsFalse(game.Views.MarkerShown);

            game.MapOpen = true;
            yield return null;
            Assert.IsTrue(game.MenusOpen);
            game.MapOpen = false;

            yield return WaitUntil(() => GameAudio.Instance.CurrentAmbience != Ambience.None, 5f, "ambience");
            var me = game.Self.Value;
            Assert.AreEqual(GameAudio.AmbienceFor(game.Map.DistrictAt(me.X, me.Z)), GameAudio.Instance.CurrentAmbience);

            game.LeaveToMenu();
            yield return WaitUntil(() => SceneManager.GetActiveScene().name == "MainMenu", 10f, "the main menu");
        }
    }
}
