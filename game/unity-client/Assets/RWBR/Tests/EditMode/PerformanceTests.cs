using NUnit.Framework;
using RWBR.Client.Character;
using RWBR.Client.Diagnostics;
using RWBR.Client.Settings;
using UnityEngine;

namespace RWBR.Tests
{
    /// <summary>Phase B task 9: character detail by distance, the frame-rate budget and automatic quality.</summary>
    public class PerformanceTests
    {
        private QualityPreset _savedQuality;
        private bool _savedAuto;

        [SetUp]
        public void Save()
        {
            GameSettings.Load();
            _savedQuality = GameSettings.Quality;
            _savedAuto = GameSettings.AutoQuality;
        }

        [TearDown]
        public void Restore()
        {
            GameSettings.SetQuality(_savedQuality);
            GameSettings.SetAutoQuality(_savedAuto);
        }

        [Test]
        public void Detail_tier_follows_distance()
        {
            Assert.AreEqual(DetailTier.Near, CharacterDetail.Tier(5f, DetailTier.Near, QualityPreset.Medium));
            Assert.AreEqual(DetailTier.Mid, CharacterDetail.Tier(25f, DetailTier.Near, QualityPreset.Medium));
            Assert.AreEqual(DetailTier.Far, CharacterDetail.Tier(80f, DetailTier.Near, QualityPreset.Medium));
            Assert.AreEqual(DetailTier.Near, CharacterDetail.Tier(5f, DetailTier.Far, QualityPreset.Medium));
        }

        [Test]
        public void Detail_tier_does_not_flicker_on_a_boundary()
        {
            float edge = CharacterDetail.NearDistance;
            Assert.AreEqual(DetailTier.Near, CharacterDetail.Tier(edge + 1f, DetailTier.Near, QualityPreset.Medium), "stays near just past the edge");
            Assert.AreEqual(DetailTier.Mid, CharacterDetail.Tier(edge - 1f, DetailTier.Mid, QualityPreset.Medium), "stays mid just inside the edge");
            float far = CharacterDetail.FarDistance;
            Assert.AreEqual(DetailTier.Mid, CharacterDetail.Tier(far + 1f, DetailTier.Mid, QualityPreset.Medium));
            Assert.AreEqual(DetailTier.Far, CharacterDetail.Tier(far - 1f, DetailTier.Far, QualityPreset.Medium));
        }

        [Test]
        public void Lower_quality_switches_detail_closer()
        {
            Assert.AreEqual(DetailTier.Mid, CharacterDetail.Tier(12f, DetailTier.Near, QualityPreset.Low));
            Assert.AreEqual(DetailTier.Near, CharacterDetail.Tier(12f, DetailTier.Near, QualityPreset.Medium));
            Assert.AreEqual(DetailTier.Near, CharacterDetail.Tier(20f, DetailTier.Near, QualityPreset.High));
            Assert.AreEqual(DetailTier.Far, CharacterDetail.Tier(30f, DetailTier.Near, QualityPreset.Low));
        }

        [Test]
        public void Frame_rate_budget()
        {
            Assert.AreEqual(30, PerformanceBudget.TargetFps(QualityPreset.Low));
            Assert.AreEqual(30, PerformanceBudget.TargetFps(QualityPreset.Medium));
            Assert.AreEqual(60, PerformanceBudget.TargetFps(QualityPreset.High));
            Assert.IsTrue(PerformanceBudget.FrameRateMet(29.5f, 40f, 30), "a steady 30 with a few slow frames");
            Assert.IsFalse(PerformanceBudget.FrameRateMet(24f, 45f, 30), "average too low");
            Assert.IsFalse(PerformanceBudget.FrameRateMet(30f, 60f, 30), "steady stutter");
            Assert.IsTrue(PerformanceBudget.FrameRateMet(59f, 22f, 60));
            Assert.Less(PerformanceBudget.MaxBatches(QualityPreset.Low), PerformanceBudget.MaxBatches(QualityPreset.High));
        }

        [Test]
        public void Automatic_quality_steps_down_to_low_and_stops()
        {
            GameSettings.SetQuality(QualityPreset.High);
            GameSettings.SetAutoQuality(true);
            Assert.IsTrue(GameSettings.LowerQualityAutomatically());
            Assert.AreEqual(QualityPreset.Medium, GameSettings.Quality);
            Assert.IsTrue(GameSettings.LowerQualityAutomatically());
            Assert.AreEqual(QualityPreset.Low, GameSettings.Quality);
            Assert.IsFalse(GameSettings.LowerQualityAutomatically(), "nothing below Low");
            Assert.IsTrue(GameSettings.AutoQuality, "stepping down keeps automatic quality on");

            GameSettings.Load();
            Assert.AreEqual(QualityPreset.Low, GameSettings.Quality, "the lowered preset is saved");
        }

        [Test]
        public void Picking_a_preset_turns_automatic_quality_off()
        {
            GameSettings.SetAutoQuality(true);
            GameSettings.SetQuality(QualityPreset.High);
            Assert.IsFalse(GameSettings.AutoQuality);
            Assert.IsFalse(GameSettings.LowerQualityAutomatically(), "the player's choice is kept");
            Assert.AreEqual(QualityPreset.High, GameSettings.Quality);
        }

        [Test]
        public void Applying_quality_outside_play_mode_leaves_the_editor_settings_alone()
        {
            int shadows = (int)QualitySettings.shadows;
            float distance = QualitySettings.shadowDistance;
            GameSettings.ApplyQuality(QualityPreset.Low);
            Assert.AreEqual(shadows, (int)QualitySettings.shadows);
            Assert.AreEqual(distance, QualitySettings.shadowDistance);
        }
    }
}
