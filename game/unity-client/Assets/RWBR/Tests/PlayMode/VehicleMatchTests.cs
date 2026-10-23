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
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Tests
{
    /// <summary>
    /// Phase C part 4 in the game: an offline match on the city with its cars. Walk up to a car, press F, drive off
    /// with the stick (the server moves the car; the client draws it and you in your seat), then get out beside it.
    /// </summary>
    public class VehicleMatchTests
    {
        private sealed class ScriptedInput : IPlayerInputSource
        {
            public PlayerInputFrame Frame;

            public PlayerInputFrame Read()
            {
                PlayerInputFrame f = Frame;
                Frame.VehiclePressed = false; // a press lasts one frame
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
        public IEnumerator Get_in_drive_and_get_out_of_a_car()
        {
            MatchConfig cfg = LocalMatch.BotMatchConfig(CityMap.Build(), 1);
            cfg.CountdownSeconds = 1f;
            cfg.ZoneStages = new List<ZoneStage> { new ZoneStage(600, 1, 50) };
            OnlineGame.RequestedBotMatch = 1;
            OnlineGame.RequestedBotConfig = cfg;
            yield return SceneManager.LoadSceneAsync("Online", LoadSceneMode.Single);
            var game = UnityEngine.Object.FindAnyObjectByType<OnlineGame>();
            var input = new ScriptedInput();
            game.InputOverride = input;
            yield return WaitUntil(() => game.Net.Latest != null && game.Net.Latest.Phase == RWBR.Shared.Protocol.MatchPhase.Active, 15f, "the match to start");

            Match match = game.Local.Match;
            Match.Vehicle car = match.Vehicles[0];
            PlayerState me = null, bot = null;
            foreach (PlayerState p in match.Players)
                if (p.Id == game.Net.PlayerId) me = p;
                else bot = p;
            bot.Position = new SVector3(-250, 0, -250);
            me.Motor = MotorState.StandingAt(car.State.Position + new SVector3(-2.2f, 0, 0));
            yield return WaitUntil(() => game.EnterableVehicle().HasValue, 5f, "being next to a car");
            Assert.AreEqual(match.Vehicles.Count, game.Views.CarsShown, "every car is drawn");

            input.Frame.VehiclePressed = true;
            yield return WaitUntil(() => game.Driving, 5f, "getting in");
            SVector3 start = car.State.Position;
            input.Frame = new PlayerInputFrame { Move = Vector2.up };
            float until = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < until)
            {
                bot.Position = new SVector3(-250, 0, -250);
                yield return null;
            }
            input.Frame = default;
            float driven = SVector3.Distance(start, car.State.Position);
            Assert.Greater(driven, 6f, "the stick drives the car");
            Transform carView = game.Views.CarTransform(car.Id);
            Assert.Less(Vector3.Distance(carView.position, game.Views.SelfTransform.position), 2f, "you are drawn in your seat");
            Assert.Greater(game.SelfVehicle.Value.Speed, 1f);

            input.Frame.VehiclePressed = true;
            yield return WaitUntil(() => !game.InVehicle, 5f, "getting out");
            Assert.IsNull(car.Driver);
            Assert.IsFalse(VehicleSim.Touches(car.State, me.Position, 0f));

            game.LeaveToMenu();
            yield return WaitUntil(() => SceneManager.GetActiveScene().name == "MainMenu", 10f, "the main menu");
        }
    }
}
