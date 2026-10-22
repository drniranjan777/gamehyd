using System;
using NUnit.Framework;
using RWBR.Client.Accessibility;
using RWBR.Client.Diagnostics;
using RWBR.Client.Settings;
using UnityEngine;

namespace RWBR.Tests
{
    public class AccessibilityAndTelemetryTests
    {
        [Serializable]
        private sealed class EventDto
        {
            public string type;
            public string at;
        }

        [Serializable]
        private sealed class BatchDto
        {
            public string sessionId;
            public string appVersion;
            public string platform;
            public string deviceModel;
            public EventDto[] events;
        }

        [Test]
        public void Telemetry_batches_are_valid_json_with_escaped_text()
        {
            string crash = "{\"type\":\"CRASH\",\"at\":\"2026-10-22T10:00:00.000Z\",\"data\":{\"message\":" + Telemetry.Json("Bad \"quote\"\nline\\") + "}}";
            string body = Telemetry.BatchJson("0d9f6a3e-8c1b-4f7a-9e2d-5b6c7a8d9e0f", "ANDROID", "Moto \"G\"", new[] { crash });
            BatchDto b = JsonUtility.FromJson<BatchDto>(body);
            Assert.AreEqual("0.6.0", b.appVersion);
            Assert.AreEqual("Moto \"G\"", b.deviceModel);
            Assert.AreEqual("CRASH", b.events[0].type);
            Assert.AreEqual("\"a\\u0001\"", Telemetry.Json("a\u0001"));
        }

        [Test]
        public void Sound_cues_point_toward_the_shot_and_fade_with_distance()
        {
            var listener = new GameObject("listener").transform;
            try
            {
                SoundCues.Clear();
                SoundCues.Add(new Vector3(10, 0, 0), 100f); // to the right
                Assert.IsTrue(FindCue(listener, out float angle, out float near));
                Assert.AreEqual(90f, angle * Mathf.Rad2Deg, 0.5f);

                SoundCues.Clear();
                SoundCues.Add(new Vector3(0, 0, -80), 100f); // behind, farther
                Assert.IsTrue(FindCue(listener, out angle, out float far));
                Assert.AreEqual(180f, Mathf.Abs(angle * Mathf.Rad2Deg), 0.5f);
                Assert.Less(far, near);

                SoundCues.Clear();
                SoundCues.Add(new Vector3(0, 0, 300), 100f); // out of earshot
                Assert.IsFalse(FindCue(listener, out _, out _));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(listener.gameObject);
                SoundCues.Clear();
            }
        }

        private static bool FindCue(Transform listener, out float angle, out float strength)
        {
            for (int i = 0; i < 16; i++)
                if (SoundCues.TryGet(i, listener, out angle, out strength)) return true;
            angle = strength = 0;
            return false;
        }

        [Test]
        public void Accessibility_settings_are_clamped()
        {
            float scale = GameSettings.UiScale;
            bool cues = GameSettings.VisualSoundCues, calm = GameSettings.ReduceFlashing;
            try
            {
                GameSettings.SetAccessibility(9f, true, true);
                Assert.AreEqual(GameSettings.MaxUiScale, GameSettings.UiScale);
                GameSettings.SetAccessibility(0.1f, false, false);
                Assert.AreEqual(GameSettings.MinUiScale, GameSettings.UiScale);
            }
            finally
            {
                GameSettings.SetAccessibility(scale, cues, calm);
            }
        }
    }
}
