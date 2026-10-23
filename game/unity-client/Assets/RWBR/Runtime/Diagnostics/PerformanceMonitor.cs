using System;
using System.Globalization;
using System.IO;
using RWBR.Client.Settings;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace RWBR.Client.Diagnostics
{
    /// <summary>
    /// Frame-time, draw-call and memory capture for the device matrix (BRD §6, §14). Every report interval it appends
    /// one JSON line to <c>perf.jsonl</c> in persistent data (pull with adb / Xcode) and logs it, checks the window
    /// against <see cref="PerformanceBudget"/>, and when automatic quality is on and the budget is missed twice in a
    /// row, lowers the quality preset one step. The first window after loading is reported but never counts as a miss.
    /// Draw-call counters exist in the editor and development builds only; release builds report them as null.
    /// </summary>
    public sealed class PerformanceMonitor : MonoBehaviour
    {
        [SerializeField] private float reportIntervalSeconds = 10f;

        private readonly float[] _samples = new float[1200];
        private int _count;
        private float _windowStart;
        private float _smoothedMs = 16.7f;
        private ProfilerRecorder _batches, _setPass, _triangles;
        private long _batchSum, _batchMax, _setPassSum, _triangleSum;
        private int _renderFrames, _misses;

        public float SmoothedFrameMs => _smoothedMs;
        public float LastP95Ms { get; private set; }
        public float LastP99Ms { get; private set; }
        public string LastReport { get; private set; } = "";
        public int ReportsWritten { get; private set; }
        /// <summary>Average batches per frame in the last window; -1 where the counter is unavailable (release builds).</summary>
        public int LastAvgBatches { get; private set; } = -1;
        public int LastMaxBatches { get; private set; } = -1;
        /// <summary>Whether the last window met the frame-rate and batch budget for its quality preset.</summary>
        public bool LastWithinBudget { get; private set; } = true;
        /// <summary>Times this monitor lowered the quality preset automatically.</summary>
        public int QualityStepDowns { get; private set; }
        public static string LogPath => Path.Combine(Application.persistentDataPath, "perf.jsonl");

        private void OnEnable()
        {
            _windowStart = Time.unscaledTime;
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        }

        private void OnDisable()
        {
            _batches.Dispose();
            _setPass.Dispose();
            _triangles.Dispose();
        }

        private void Update()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            _smoothedMs = Mathf.Lerp(_smoothedMs, ms, 0.05f);
            if (_count < _samples.Length) _samples[_count++] = ms;
            if (_batches.Valid && _batches.LastValue > 0)
            {
                long b = _batches.LastValue;
                _batchSum += b;
                if (b > _batchMax) _batchMax = b;
                _setPassSum += _setPass.Valid ? _setPass.LastValue : 0;
                _triangleSum += _triangles.Valid ? _triangles.LastValue : 0;
                _renderFrames++;
            }
            if (Time.unscaledTime - _windowStart >= reportIntervalSeconds) Report();
        }

        /// <summary>Ends the current window now (tests; the device matrix uses the interval).</summary>
        public void ReportNow() => Report();

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
            float fps = _count / Mathf.Max(window, 1e-3f);

            QualityPreset quality = GameSettings.Quality;
            int targetFps = PerformanceBudget.TargetFps(quality);
            bool counted = _renderFrames > 0;
            LastAvgBatches = counted ? (int)(_batchSum / _renderFrames) : -1;
            LastMaxBatches = counted ? (int)_batchMax : -1;
            bool batchesMet = !counted || LastAvgBatches <= PerformanceBudget.MaxBatches(quality);
            LastWithinBudget = PerformanceBudget.FrameRateMet(fps, LastP95Ms, targetFps) && batchesMet;

            var inv = CultureInfo.InvariantCulture;
            string render = counted
                ? string.Format(inv, "{0},\"maxBatches\":{1},\"setPass\":{2},\"triangles\":{3}", LastAvgBatches, LastMaxBatches,
                    _setPassSum / _renderFrames, _triangleSum / _renderFrames)
                : "null,\"maxBatches\":null,\"setPass\":null,\"triangles\":null";
            LastReport = string.Format(inv,
                "{{\"utc\":\"{0:o}\",\"device\":\"{1}\",\"gpu\":\"{2}\",\"ramMB\":{3},\"quality\":\"{4}\",\"autoQuality\":{5},\"frames\":{6},\"seconds\":{7:0.0}," +
                "\"fps\":{8:0.0},\"targetFps\":{9},\"avgMs\":{10:0.00},\"p95Ms\":{11:0.00},\"p99Ms\":{12:0.00},\"maxMs\":{13:0.00}," +
                "\"batches\":{14},\"withinBudget\":{15}," +
                "\"allocatedMB\":{16:0.0},\"reservedMB\":{17:0.0},\"monoMB\":{18:0.0}}}",
                DateTime.UtcNow, Escape(SystemInfo.deviceModel), Escape(SystemInfo.graphicsDeviceName), SystemInfo.systemMemorySize,
                quality, GameSettings.AutoQuality ? "true" : "false", _count, window, fps, targetFps, sum / _count, LastP95Ms, LastP99Ms,
                sorted[_count - 1], render, LastWithinBudget ? "true" : "false",
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

            // The first window holds the scene load, so it never counts against the budget.
            if (ReportsWritten > 0) _misses = LastWithinBudget ? 0 : _misses + 1;
            if (_misses >= PerformanceBudget.MissesBeforeStepDown && GameSettings.LowerQualityAutomatically())
            {
                QualityStepDowns++;
                _misses = 0;
                Debug.Log($"RWBR perf: below budget twice in a row, quality lowered to {GameSettings.Quality}");
            }
            ReportsWritten++;
            _count = 0;
            _batchSum = _batchMax = _setPassSum = _triangleSum = 0;
            _renderFrames = 0;
            _windowStart = Time.unscaledTime;
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
