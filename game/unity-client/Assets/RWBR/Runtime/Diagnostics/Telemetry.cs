using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RWBR.Client.Settings;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace RWBR.Client.Diagnostics
{
    /// <summary>
    /// Beta telemetry (Phase 6): session start/end, crashes (unhandled exceptions), FPS samples by device and
    /// disconnects, batched to POST /telemetry/events every 30 s and when the app is paused or closed. Events wait in
    /// memory (at most 300) while the platform is unreachable. Nothing personal is sent: no names, no free text.
    /// Off in batch mode (tests, builds) unless RWBR_TELEMETRY=1.
    /// </summary>
    public sealed class Telemetry : MonoBehaviour
    {
        public const string AppVersion = "0.6.0";
        private const float FlushSeconds = 30f;
        private const float FpsWindowSeconds = 60f;
        private const int MaxQueued = 300;
        private const int MaxCrashesPerSession = 5;

        private static Telemetry _instance;
        private static string _accessToken;
        private readonly List<string> _queue = new List<string>();
        private readonly List<float> _frameMs = new List<float>(4096);
        private string _sessionId;
        private float _sessionStart, _lastFlush, _fpsWindowStart;
        private int _crashes;
        private bool _sending;

        /// <summary>Events waiting to be sent (for tests and the debug overlay).</summary>
        public static int Pending => _instance != null ? _instance._queue.Count : 0;

        public static string PlatformUrl => PlayerPrefs.GetString("rwbr.platform", RWBR.Client.Online.OnlineGame.DefaultPlatform);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Application.isBatchMode && Environment.GetEnvironmentVariable("RWBR_TELEMETRY") != "1") return;
            Begin();
        }

        /// <summary>Starts the session (idempotent). Called automatically outside batch mode.</summary>
        public static Telemetry Begin()
        {
            if (_instance != null) return _instance;
            var go = new GameObject("RWBR Telemetry");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<Telemetry>();
            return _instance;
        }

        /// <summary>Attaches the signed-in account to later batches.</summary>
        public static void SetAccessToken(string token) => _accessToken = token;

        public static void Disconnect(string reason, string matchId, bool? reconnected = null)
        {
            if (_instance == null) return;
            var data = new StringBuilder("{\"reason\":").Append(Json(Truncate(reason, 60)));
            if (!string.IsNullOrEmpty(matchId)) data.Append(",\"matchId\":").Append(Json(matchId));
            if (reconnected.HasValue) data.Append(",\"reconnected\":").Append(reconnected.Value ? "true" : "false");
            _instance.Enqueue("DISCONNECT", data.Append('}').ToString());
        }

        private void Awake()
        {
            _sessionId = Guid.NewGuid().ToString();
            _sessionStart = _lastFlush = _fpsWindowStart = Time.unscaledTime;
            Application.logMessageReceived += OnLog;
            Enqueue("SESSION_START", $"{{\"os\":{Json(Truncate(SystemInfo.operatingSystem, 60))},\"quality\":{Json(GameSettings.Quality.ToString().ToUpperInvariant())}}}");
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            _frameMs.Add(Time.unscaledDeltaTime * 1000f);
            if (Time.unscaledTime - _fpsWindowStart >= FpsWindowSeconds) SampleFps();
            if (Time.unscaledTime - _lastFlush >= FlushSeconds) Flush();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) EndSession();
        }

        private void OnApplicationQuit() => EndSession();

        private void EndSession()
        {
            SampleFps();
            Enqueue("SESSION_END", $"{{\"durationSeconds\":{(int)(Time.unscaledTime - _sessionStart)}}}");
            Flush();
        }

        private void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception || _crashes >= MaxCrashesPerSession) return;
            _crashes++;
            Enqueue("CRASH", $"{{\"message\":{Json(Truncate(condition, 300))},\"where\":{Json(Truncate(SceneManager.GetActiveScene().name, 200))},\"stack\":{Json(Truncate(stack, 2000))}}}");
        }

        /// <summary>Average FPS and the 5th-percentile FPS (from the 95th-percentile frame time) over the window.</summary>
        private void SampleFps()
        {
            float seconds = Time.unscaledTime - _fpsWindowStart;
            if (_frameMs.Count < 10 || seconds < 1f)
            {
                _frameMs.Clear();
                _fpsWindowStart = Time.unscaledTime;
                return;
            }
            float[] sorted = _frameMs.ToArray();
            Array.Sort(sorted);
            float p95Ms = sorted[(int)(sorted.Length * 0.95f)];
            var inv = CultureInfo.InvariantCulture;
            string scene = SceneManager.GetActiveScene().name switch
            {
                "Online" => "ONLINE",
                "Greybox" => "PRACTICE",
                _ => "MENU",
            };
            Enqueue("FPS_SAMPLE", string.Format(inv,
                "{{\"avgFps\":{0:0.0},\"p5Fps\":{1:0.0},\"seconds\":{2},\"scene\":\"{3}\",\"quality\":\"{4}\"}}",
                Mathf.Min(1000f, sorted.Length / seconds), Mathf.Min(1000f, 1000f / Mathf.Max(1f, p95Ms)), Mathf.Clamp((int)seconds, 1, 3600),
                scene, GameSettings.Quality.ToString().ToUpperInvariant()));
            _frameMs.Clear();
            _fpsWindowStart = Time.unscaledTime;
        }

        private void Enqueue(string type, string dataJson)
        {
            if (_queue.Count >= MaxQueued) _queue.RemoveAt(0);
            _queue.Add($"{{\"type\":\"{type}\",\"at\":\"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\",\"data\":{dataJson}}}");
        }

        /// <summary>Sends up to 100 queued events. Failed batches go back to the front of the queue.</summary>
        public void Flush()
        {
            _lastFlush = Time.unscaledTime;
            if (_sending || _queue.Count == 0) return;
            int n = Math.Min(100, _queue.Count);
            List<string> batch = _queue.GetRange(0, n);
            _queue.RemoveRange(0, n);
            string body = BatchJson(_sessionId, PlatformName(), Truncate(SystemInfo.deviceModel, 100), batch);
            StartCoroutine(Send(body, batch));
        }

        public static string BatchJson(string sessionId, string platform, string deviceModel, IReadOnlyList<string> events) =>
            $"{{\"sessionId\":\"{sessionId}\",\"appVersion\":\"{AppVersion}\",\"platform\":\"{platform}\",\"deviceModel\":{Json(string.IsNullOrEmpty(deviceModel) ? "unknown" : deviceModel)},\"events\":[{string.Join(",", events)}]}}";

        private IEnumerator Send(string body, List<string> batch)
        {
            _sending = true;
            using var req = new UnityWebRequest(PlatformUrl.TrimEnd('/') + "/telemetry/events", "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 10,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            if (_accessToken != null) req.SetRequestHeader("Authorization", "Bearer " + _accessToken);
            yield return req.SendWebRequest();
            _sending = false;
            // Network or server trouble: keep the events for the next flush. A 422 means a bug here; drop them.
            if (req.result == UnityWebRequest.Result.ConnectionError || req.responseCode >= 500 || req.responseCode == 429)
                _queue.InsertRange(0, batch.GetRange(0, Math.Min(batch.Count, MaxQueued - _queue.Count)));
        }

        public static string PlatformName() => Application.platform switch
        {
            RuntimePlatform.Android => "ANDROID",
            RuntimePlatform.IPhonePlayer => "IOS",
            RuntimePlatform.WindowsPlayer => "WINDOWS",
            RuntimePlatform.OSXPlayer => "MACOS",
            RuntimePlatform.LinuxPlayer => "LINUX",
            _ => "EDITOR",
        };

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max);

        /// <summary>JSON string literal with the escapes JSON requires.</summary>
        public static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
