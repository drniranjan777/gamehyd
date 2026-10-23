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
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Tests
{
    /// <summary>
    /// Phase C part 2 in the game: in an offline match the player's inventory shows what the server holds, a dropped
    /// bandage appears on the ground and can be picked up by id, a frag explodes and a smoke grenade makes a cloud,
    /// and the bag screen draws.
    /// </summary>
    public class ItemMatchTests
    {
        private sealed class ScriptedInput : IPlayerInputSource
        {
            public PlayerInputFrame Frame;

            public PlayerInputFrame Read()
            {
                PlayerInputFrame f = Frame;
                Frame.ThrowPressed = false; // a press lasts one frame
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

        private static IEnumerator Wait(float seconds, Action everyFrame)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                everyFrame();
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Inventory_drops_pickups_and_grenades_work_in_a_match()
        {
            MatchConfig cfg = LocalMatch.BotMatchConfig(MapLayout.GreyboxBlock(100f), 1);
            cfg.CountdownSeconds = 1f;
            cfg.ZoneStages = new List<ZoneStage> { new ZoneStage(600, 1, 50) };
            cfg.MaxMatchSeconds = 900f;
            OnlineGame.RequestedBotMatch = 1;
            OnlineGame.RequestedBotConfig = cfg;
            yield return SceneManager.LoadSceneAsync("Online", LoadSceneMode.Single);
            var game = UnityEngine.Object.FindAnyObjectByType<OnlineGame>();
            var input = new ScriptedInput();
            game.InputOverride = input;
            yield return WaitUntil(() => game.Net.Latest != null && game.Net.Latest.Phase == MatchPhase.Active, 15f, "the match to start");

            Match match = game.Local.Match;
            PlayerState me = null, bot = null;
            foreach (PlayerState p in match.Players)
                if (p.Id == game.Net.PlayerId) me = p;
                else bot = p;
            // Keep the bot out of the way, in a far corner.
            Action park = () => bot.Position = new SVector3(-90, 0, -90);
            park();
            me.Combat.SetCount(ItemKind.Bandage, 2);
            me.Combat.SetCount(ItemKind.Frag, 1);
            me.Combat.SetCount(ItemKind.Smoke, 1);
            yield return WaitUntil(() => game.Inventory != null && game.Inventory.Count(ItemKind.Frag) == 1, 5f, "the inventory to arrive");
            Assert.AreEqual(2, game.Inventory.Count(ItemKind.Bandage));

            // Drop one bandage: it lies on the ground, then take it back by id.
            game.DropItem(ItemKind.Bandage, 1);
            yield return WaitUntil(() => game.Views.GroundItemsShown == 1, 5f, "the dropped bandage");
            Assert.AreEqual(1, game.Inventory.Count(ItemKind.Bandage));
            OnlineGame.NearbyItem dropped = game.NearbyItems().Find(n => n.Kind == ItemKind.Bandage);
            Assert.GreaterOrEqual(dropped.Id, ProtocolInfo.FirstGroundItemId);
            Assert.IsTrue(dropped.Useful);
            game.PickUp(dropped.Id);
            yield return WaitUntil(() => game.Inventory.Count(ItemKind.Bandage) == 2 && game.Views.GroundItemsShown == 0, 5f, "picking it up");

            // The bag screen draws (IMGUI) without errors.
            game.InventoryOpen = true;
            yield return Wait(0.3f, park);
            Assert.IsTrue(game.MenusOpen);
            game.InventoryOpen = false;

            // Throw the frag forward: it goes off after its fuse.
            game.SelectThrowable(ItemKind.Frag);
            input.Frame.ThrowPressed = true;
            yield return WaitUntil(() => game.Inventory.Count(ItemKind.Frag) == 0, 3f, "the frag to be thrown");
            yield return Wait(Match.FragFuseSeconds + 0.6f, park);
            Assert.AreEqual(1, game.Views.ExplosionsShown, "the explosion was shown");
            Assert.IsTrue(me.Alive);

            // G with no frag left throws the smoke instead.
            input.Frame.ThrowPressed = true;
            yield return WaitUntil(() => game.Inventory.Count(ItemKind.Smoke) == 0, 3f, "the smoke to be thrown");
            yield return Wait(Match.SmokeDelaySeconds + 0.6f, park);
            Assert.AreEqual(1, game.Views.SmokeCloudsShown, "a smoke cloud");

            game.LeaveToMenu();
            yield return WaitUntil(() => SceneManager.GetActiveScene().name == "MainMenu", 10f, "the main menu");
        }
    }
}
