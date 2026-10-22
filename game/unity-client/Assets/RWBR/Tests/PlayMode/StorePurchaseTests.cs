using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using NUnit.Framework;
using RWBR.Client.Online;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace RWBR.Tests
{
    /// <summary>
    /// The game's store and account flows against the real platform API (fresh database, dev purchases on):
    /// catalogue, a coin purchase without coins, a real-money purchase through the dev store, deletion and cancel.
    /// Needs TEST_ADMIN_DATABASE_URL, node and the built API (apps/api/dist); ignored otherwise.
    /// </summary>
    public class StorePurchaseTests
    {
        private Process _api;
        private string _dbName;
        private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));

        private static string Hex(int bytes)
        {
            byte[] b = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
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

        [TearDown]
        public void TearDown()
        {
            try { if (_api != null && !_api.HasExited) _api.Kill(); }
            catch (Exception) { }
            if (_dbName != null)
            {
                try { RunNode($"--import tsx src/testdb-cli.ts drop {_dbName}", Path.Combine(Repo, "database")); }
                catch (Exception e) { Debug.LogWarning(e.Message); }
            }
            PlayerPrefs.DeleteKey("rwbr.guest.deviceId");
            PlayerPrefs.DeleteKey("rwbr.guest.deviceSecret");
        }

        private static IEnumerator Await(Task task, float seconds = 20)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup > until) Assert.Fail("timed out");
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Buys_with_the_dev_store_and_schedules_then_cancels_deletion()
        {
            string apiMain = Path.Combine(Repo, "apps", "api", "dist", "main.js");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_ADMIN_DATABASE_URL")) || !File.Exists(apiMain))
                Assert.Ignore("Needs TEST_ADMIN_DATABASE_URL and apps/api/dist.");

            _dbName = "rwbr_unity_store_" + UnityEngine.Random.Range(100000, 999999);
            string urls = RunNode($"--import tsx src/testdb-cli.ts create {_dbName}", Path.Combine(Repo, "database"));
            string appUrl = JsonUtility.FromJson<DbUrls>(urls.Trim()).appUrl;
            int port = 3990 + UnityEngine.Random.Range(0, 9);
            string devSecret = Hex(24);
            var psi = new ProcessStartInfo("node", $"\"{apiMain}\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Repo };
            foreach ((string k, string v) in new[]
                     {
                         ("PORT", port.ToString()), ("DATABASE_URL", appUrl), ("JWT_SECRET", Hex(32)), ("SERVER_SHARED_SECRET", Hex(32)),
                         ("TICKET_SECRET_HEX", Hex(32)), ("ADMIN_TOTP_KEY_HEX", Hex(32)), ("DEV_PURCHASES_ENABLED", "true"),
                         ("DEV_PURCHASE_SECRET", devSecret), ("LOG_REQUESTS", "false"), ("RECONCILE_INTERVAL_SECONDS", "0"),
                     })
                psi.EnvironmentVariables[k] = v;
            _api = Process.Start(psi);
            yield return new WaitForSecondsRealtime(2.5f);

            PlayerPrefs.DeleteKey("rwbr.guest.deviceId");
            var api = new PlatformApi($"http://127.0.0.1:{port}/api/v1");
            yield return Await(api.SignInAsync("StoreTester"));

            Task<CatalogDto> catalog = api.CatalogAsync();
            yield return Await(catalog);
            Assert.IsTrue(catalog.Result.storeEnabled);
            CatalogItemDto outfit = catalog.Result.items.Single(i => i.productKey == "outfit_lakeside_runner");
            CatalogItemDto banner = catalog.Result.items.Single(i => i.productKey == "banner_irani_chai");

            var flow = new StoreFlow(api, new DevStorePurchaser(devSecret));
            Task<OrderDto> poor = flow.BuyAsync(banner);
            yield return Await(poor);
            Assert.IsTrue(poor.IsFaulted);
            Assert.AreEqual("INSUFFICIENT_FUNDS", ((ApiException)poor.Exception.InnerException).Code);

            Task<OrderDto> bought = flow.BuyAsync(outfit);
            yield return Await(bought);
            Assert.IsFalse(bought.IsFaulted, bought.Exception?.InnerException?.Message);
            Assert.AreEqual("FULFILLED", bought.Result.status);
            Task<InventoryDto> inv = api.InventoryAsync();
            yield return Await(inv);
            Assert.IsTrue(inv.Result.items.Any(i => i.cosmeticKey == "outfit_lakeside_runner"));

            // A forged receipt (wrong secret) is refused by the server.
            Task<OrderDto> forged = new StoreFlow(api, new DevStorePurchaser("not-the-secret-000000000000")).BuyAsync(
                catalog.Result.items.Single(i => i.productKey == "gems_100"));
            yield return Await(forged);
            Assert.AreEqual("RECEIPT_REJECTED", ((ApiException)forged.Exception.InnerException).Code);

            Task<DeletionStatusDto> del = api.RequestDeletionAsync();
            yield return Await(del);
            Assert.AreEqual("PENDING_DELETION", del.Result.status);
            Assert.IsFalse(string.IsNullOrEmpty(del.Result.scheduledFor));
            Task<DeletionStatusDto> keep = api.CancelDeletionAsync();
            yield return Await(keep);
            Assert.AreEqual("ACTIVE", keep.Result.status);

            // The game's telemetry batches are accepted by the API's strict schema.
            string fps = "{\"type\":\"FPS_SAMPLE\",\"at\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") +
                         "\",\"data\":{\"avgFps\":57.5,\"p5Fps\":41.0,\"seconds\":60,\"scene\":\"MENU\",\"quality\":\"MEDIUM\"}}";
            string body = RWBR.Client.Diagnostics.Telemetry.BatchJson(Guid.NewGuid().ToString(), "EDITOR", SystemInfo.deviceModel, new[] { fps });
            using var post = new UnityEngine.Networking.UnityWebRequest($"http://127.0.0.1:{port}/api/v1/telemetry/events", "POST")
            {
                uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body)),
                downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer(),
            };
            post.SetRequestHeader("Content-Type", "application/json");
            yield return post.SendWebRequest();
            Assert.AreEqual(202, post.responseCode, post.downloadHandler.text);
        }

        [Serializable]
        private sealed class DbUrls
        {
            public string appUrl;
            public string migratorUrl;
        }
    }
}
