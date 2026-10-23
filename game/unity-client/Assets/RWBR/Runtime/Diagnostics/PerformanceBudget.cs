using RWBR.Client.Settings;

namespace RWBR.Client.Diagnostics
{
    /// <summary>
    /// The frame-rate and draw-call budget per quality preset (Phase B task 9: 30 FPS sustained on mid-range Android,
    /// 60 on recent iPhones). A report window meets the budget when the average frame rate is within 10 % of the
    /// target and the 95th-percentile frame is no slower than 1.5 target frames, so a few hitches are allowed but
    /// steady stutter is not.
    /// </summary>
    public static class PerformanceBudget
    {
        /// <summary>Missed windows in a row before automatic quality steps down one preset.</summary>
        public const int MissesBeforeStepDown = 2;

        public static int TargetFps(QualityPreset quality) => quality == QualityPreset.High ? 60 : 30;

        /// <summary>Batches per frame the preset should stay under (the world is merged into 64 m chunks).</summary>
        public static int MaxBatches(QualityPreset quality)
        {
            switch (quality)
            {
                case QualityPreset.Low: return 120;
                case QualityPreset.High: return 250;
                default: return 180;
            }
        }

        public static bool FrameRateMet(float fps, float p95Ms, int targetFps) =>
            fps >= targetFps * 0.9f && p95Ms <= 1000f / targetFps * 1.5f;
    }
}
