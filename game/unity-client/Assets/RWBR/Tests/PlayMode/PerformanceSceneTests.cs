using System.Collections;
using NUnit.Framework;
using RWBR.Client.Character;
using RWBR.Client.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RWBR.Tests
{
    /// <summary>Phase B task 9 in the practice scene: the camera reports its position and telemetry carries the budget.</summary>
    public class PerformanceSceneTests
    {
        [UnityTest]
        public IEnumerator Practice_scene_reports_budget_and_draw_calls()
        {
            yield return SceneManager.LoadSceneAsync("Greybox", LoadSceneMode.Single);
            float until = Time.time + 1f;
            while (Time.time < until) yield return null;

            Assert.IsTrue(CharacterDetail.Viewer.HasValue, "the camera reports its position for character detail");
            Assert.Less(Vector3.Distance(CharacterDetail.Viewer.Value, Camera.main.transform.position), 0.01f);

            PerformanceMonitor monitor = Object.FindAnyObjectByType<PerformanceMonitor>();
            Assert.IsNotNull(monitor);
            monitor.ReportNow();
            string report = monitor.LastReport;
            Debug.Log("RWBR perf test: " + report);
            StringAssert.Contains("\"targetFps\":", report);
            StringAssert.Contains("\"withinBudget\":", report);
            StringAssert.Contains("\"autoQuality\":", report);
            StringAssert.Contains("\"batches\":", report);
            // Batch counters exist only where the scene is rendered (not in -nographics runs).
            if (monitor.LastAvgBatches >= 0)
                Assert.LessOrEqual(monitor.LastAvgBatches, PerformanceBudget.MaxBatches(RWBR.Client.Settings.QualityPreset.High));
        }
    }
}
