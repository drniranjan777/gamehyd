using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using RWBR.Client.Online;
using RWBR.Client.Player;
using RWBR.Shared.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace RWBR.Tests
{
    /// <summary>
    /// End to end: the real .NET match server and one headless bot run as processes; the Unity client joins as the
    /// second player, walks with prediction, and plays until the server declares a result.
    /// Needs `dotnet build -c Release` of game/dedicated-server first; ignored otherwise.
    /// </summary>
    public class OnlineMatchTests
    {
        private sealed class ScriptedInput : IPlayerInputSource
        {
            public PlayerInputFrame Frame;
            public PlayerInputFrame Read() => Frame;
        }

        private Process _server, _bot;

        private static string Exe(string project)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "dedicated-server", "artifacts", "bin", project, "release"));
            string exe = Path.Combine(root, project + (Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : ""));
            return File.Exists(exe) ? exe : null;
        }

        private static Process Start(string exe, string args) => Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        [TearDown]
        public void StopProcesses()
        {
            foreach (Process p in new[] { _bot, _server })
            {
                try { if (p != null && !p.HasExited) p.Kill(); }
                catch (Exception) { }
            }
            PlayerPrefs.DeleteKey("rwbr.server");
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
        public IEnumerator Client_joins_predicts_movement_and_receives_the_server_result()
        {
            string serverExe = Exe("RWBR.MatchServer"), botExe = Exe("RWBR.Bots");
            if (serverExe == null || botExe == null) Assert.Ignore("Build game/dedicated-server (Release) to run this test.");

            int port = 47000 + UnityEngine.Random.Range(0, 1000);
            _server = Start(serverExe, $"--port {port} --players 2 --fast");
            _bot = Start(botExe, $"--port {port} --count 1 --timeout 90");
            PlayerPrefs.SetString("rwbr.server", $"127.0.0.1:{port}");

            yield return SceneManager.LoadSceneAsync("Online", LoadSceneMode.Single);
            OnlineGame game = UnityEngine.Object.FindAnyObjectByType<OnlineGame>();
            Assert.IsNotNull(game);
            var input = new ScriptedInput();
            game.InputOverride = input;

            yield return WaitUntil(() => game.Prediction != null && game.Prediction.Initialized, 15, "welcome and first snapshot");
            Assert.AreEqual("greybox-block-40-v2", game.Map.Version, "client did not adopt the server's map");
            yield return WaitUntil(() => game.Net.Latest.Phase == MatchPhase.Active, 15, "countdown to finish");

            Vector3 start = game.Views.SelfTransform.position;
            input.Frame = new PlayerInputFrame { Move = Vector2.up };
            float until = Time.realtimeSinceStartup + 1.5f;
            while (Time.realtimeSinceStartup < until && game.SelfAlive) yield return null;
            input.Frame = default;

            float moved = Vector3.Distance(start, game.Views.SelfTransform.position);
            Debug.Log($"RWBR online test: moved {moved:0.00} m, corrections {game.Prediction.Corrections}, max error {game.Prediction.MaxError:0.000} m, " +
                      $"RTT {game.Net.RoundTripMs:0} ms, snapshots {game.Net.SnapshotsDecoded} ({game.Net.DeltaSnapshots} delta)");
            if (game.SelfAlive) Assert.Greater(moved, 3f, "predicted movement did not happen");
            Assert.Less(game.Prediction.MaxError, 0.5f, "large prediction errors on loopback");

            yield return WaitUntil(() => game.Net.Result != null, 70, "the server's match result");
            Assert.AreEqual(2, game.Net.Result.Placements.Count);
            Assert.Greater(game.Net.DeltaSnapshots, game.Net.SnapshotsDecoded / 2, "most snapshots should be deltas");
            Assert.AreEqual(0, game.Net.MissingBaselines);
        }
    }
}
