using System;
using System.Globalization;
using System.IO;
using RWBR.Client.Settings;
using UnityEngine;
using UnityEngine.Profiling;

namespace RWBR.Client.Diagnostics
{
    /// <summary>
    /// Frame-time and memory capture for the device matrix (BRD §6, §14). Every report interval it appends one
    /// JSON line to <c>perf.jsonl</c> in persistent data (pull with adb / Xcode) and logs it.
    /// </summary>
    public sealed class PerformanceMonitor : MonoBehaviour
    {
        [SerializeField] private float reportIntervalSeconds = 10f;

        private readonly float[] _samples = new float[1200];
        private int _count;
        private float _windowStart;
        private float _smoothedMs = 16.7f;

        public float SmoothedFrameMs => _smoothedMs;
        public float LastP95Ms { get; private set; }
        public float LastP99Ms { get; private set; }
        public string LastReport { get; private set; } = "";
        public int ReportsWritten { get; private set; }
        public static string LogPath => Path.Combine(Application.persistentDataPath, "perf.jsonl");

        private void OnEnable() => _windowStart = Time.unscaledTime;

        private void Update()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            _smoothedMs = Mathf.Lerp(_smoothedMs, ms, 0.05f);
            if (_count < _samples.Length) _samples[_count++] = ms;
            if (Time.unscaledTime - _windowStart >= reportIntervalSeconds) Report();
        }

        private void Report()
        {
            if (_count == 0) return;
            float[] sorted = new float[_count];
            Array.Copy(_samples, sorted, _count);
            Array.Sort(sorted);
            float sum = 0;
            foreach (float s in sorted) sum += s;
            float window = Time.unscaledTime - _windowStart;
            LastP95Ms = sorted[(int)(_count * 0.95f)];
            LastP99Ms = sorted[Mathf.Min(_count - 1, (int)(_count * 0.99f))];

            var inv = CultureInfo.InvariantCulture;
            LastReport = string.Format(inv,
                "{{\"utc\":\"{0:o}\",\"device\":\"{1}\",\"gpu\":\"{2}\",\"ramMB\":{3},\"quality\":\"{4}\",\"frames\":{5},\"seconds\":{6:0.0}," +
                "\"fps\":{7:0.0},\"avgMs\":{8:0.00},\"p95Ms\":{9:0.00},\"p99Ms\":{10:0.00},\"maxMs\":{11:0.00}," +
                "\"allocatedMB\":{12:0.0},\"reservedMB\":{13:0.0},\"monoMB\":{14:0.0}}}",
                DateTime.UtcNow, Escape(SystemInfo.deviceModel), Escape(SystemInfo.graphicsDeviceName), SystemInfo.systemMemorySize,
                GameSettings.Quality, _count, window, _count / window, sum / _count, LastP95Ms, LastP99Ms, sorted[_count - 1],
                Profiler.GetTotalAllocatedMemoryLong() / 1048576f, Profiler.GetTotalReservedMemoryLong() / 1048576f,
                Profiler.GetMonoUsedSizeLong() / 1048576f);

            Debug.Log("RWBR perf " + LastReport);
            try
            {
                File.AppendAllText(LogPath, LastReport + "\n");
            }
            catch (Exception)
            {
                // Storage unavailable: the log line above still has the data.
            }
            ReportsWritten++;
            _count = 0;
            _windowStart = Time.unscaledTime;
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
