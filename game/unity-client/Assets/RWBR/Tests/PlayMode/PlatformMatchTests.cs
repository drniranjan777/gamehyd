using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using RWBR.Client.Online;
using RWBR.Client.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace RWBR.Tests
{
    /// <summary>
    /// Whole stack: PostgreSQL (fresh database) + platform API + managed match server + one API bot. The Unity
    /// client uses Quick play (guest sign-in, matchmaking, signed seat), plays to the end and reads back its XP.
    /// Needs TEST_ADMIN_DATABASE_URL, node, the built API (apps/api/dist) and Release .NET binaries; ignored otherwise.
    /// </summary>
    public class PlatformMatchTests
    {
        private sealed class Idle : IPlayerInputSource
        {
            public PlayerInputFrame Read() => default;
        }

        private readonly Process[] _procs = new Process[3];
        private string _dbName;
        private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));

        private static Process Start(string file, string args, string cwd, params (string Key, string Value)[] env)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach ((string k, string v) in env) psi.EnvironmentVariables[k] = v;
            Process p = Process.Start(psi);
            p.OutputDataReceived += (_, __) => { };
            p.ErrorDataReceived += (_, __) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        private static string RunNode(string args, string cwd)
        {
            var psi = new ProcessStartInfo("node", args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd, RedirectStandardOutput = true };
            using Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception($"node {args} failed ({p.ExitCode})");
            return output;
        }

        private static string Hex(int bytes)
        {
            byte[] b = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Process p in _procs)
            {
                try { if (p != null && !p.HasExited) p.Kill(); }
                catch (Exception) { }
            }
            if (_dbName != null)
            {
                try { RunNode($"--import tsx src/testdb-cli.ts drop {_dbName}", Path.Combine(Repo, "database")); }
                catch (Exception e) { Debug.LogWarning(e.Message); }
            }
            PlayerPrefs.DeleteKey("rwbr.platform");
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
        public IEnumerator Quick_play_signs_in_matchmakes_plays_and_earns_xp()
        {
            string ext = Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : "";
            string bin = Path.Combine(Repo, "game", "dedicated-server", "artifacts", "bin");
            string serverExe = Path.Combine(bin, "RWBR.MatchServer", "release", "RWBR.MatchServer" + ext);
            string botExe = Path.Combine(bin, "RWBR.Bots", "release", "RWBR.Bots" + ext);
            string apiMain = Path.Combine(Repo, "apps", "api", "dist", "main.js");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_ADMIN_DATABASE_URL")) ||
                !File.Exists(serverExe) || !File.Exists(botExe) || !File.Exists(apiMain))
                Assert.Ignore("Needs TEST_ADMIN_DATABASE_URL, apps/api/dist and Release .NET binaries.");

            _dbName = "rwbr_unity_" + UnityEngine.Random.Range(100000, 999999);
            string urls = RunNode($"--import tsx src/testdb-cli.ts create {_dbName}", Path.Combine(Repo, "database"));
            string appUrl = JsonUtility.FromJson<DbUrls>(urls.Trim()).appUrl;

            int apiPort = 3950 + UnityEngine.Random.Range(0, 40), gamePort = 47800 + UnityEngine.Random.Range(0, 100);
            string serverSecret = Hex(32), ticketSecret = Hex(32), api = $"http://127.0.0.1:{apiPort}/api/v1";
            _procs[0] = Start("node", $"\"{apiMain}\"", Repo,
                ("PORT", apiPort.ToString()), ("DATABASE_URL", appUrl), ("JWT_SECRET", Hex(32)),
                ("SERVER_SHARED_SECRET", serverSecret), ("TICKET_SECRET_HEX", ticketSecret), ("ADMIN_TOTP_KEY_HEX", Hex(32)),
                ("MATCH_CAPACITY", "2"), ("MATCH_MIN_PLAYERS", "2"), ("MATCH_FILL_WAIT_SECONDS", "0"),
                ("DEFAULT_MAP_VERSION", "greybox-block-40-v2"), ("LOG_REQUESTS", "false"));
            _procs[1] = Start(serverExe, $"--api-url {api} --address 127.0.0.1:{gamePort} --port {gamePort} --fast --max-matches 1", Repo,
                ("RWBR_SERVER_SHARED_SECRET", serverSecret), ("RWBR_TICKET_SECRET_HEX", ticketSecret));
            yield return new WaitForSecondsRealtime(3f); // API boot + first heartbeat
            _procs[2] = Start(botExe, $"--api {api} --count 1 --timeout 90", Repo);

            PlayerPrefs.SetString("rwbr.platform", api);
            PlayerPrefs.SetString("rwbr.server", "127.0.0.1:1"); // keep the default direct connect harmless
            PlayerPrefs.DeleteKey("rwbr.guest.deviceId");
            PlayerPrefs.DeleteKey("rwbr.guest.deviceSecret");
            yield return SceneManager.LoadSceneAsync("Online", LoadSceneMode.Single);
            OnlineGame game = UnityEngine.Object.FindAnyObjectByType<OnlineGame>();
            game.InputOverride = new Idle();
            game.PlayerName = "UnityTester";
            game.PlatformUrl = api;
            game.StartQuickPlay();

            yield return WaitUntil(() => game.Error != null || (game.Prediction != null && game.Prediction.Initialized), 40, "a seat and the first snapshot");
            Assert.IsNull(game.Error, game.Error);
            Assert.AreEqual($"127.0.0.1:{gamePort}", game.ServerAddress);
            Assert.IsNotNull(game.ProfileBefore);
            Assert.AreEqual(0, game.ProfileBefore.xp);

            yield return WaitUntil(() => game.Net.Result != null, 90, "the match result");
            yield return WaitUntil(() => game.ProfileAfter != null, 20, "XP from the platform");
            Debug.Log($"RWBR platform test: level {game.ProfileAfter.level}, xp {game.ProfileAfter.xp}, placement result {game.Net.Result.Reason}");
            Assert.Greater(game.ProfileAfter.xp, 0, "the server's signed result did not reach the player's profile");
        }

        [Serializable]
        private sealed class DbUrls
        {
            public string appUrl;
            public string migratorUrl;
        }
    }
}
