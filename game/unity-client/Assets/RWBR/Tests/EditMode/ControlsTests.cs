using NUnit.Framework;
using RWBR.Client.Player;
using RWBR.Client.Settings;
using UnityEngine;

namespace RWBR.Tests
{
    /// <summary>Phase B part 3: touch layout editing and storage, vibration setting.</summary>
    public class ControlsTests
    {
        private string _savedLayout;
        private bool _savedVibration;

        [SetUp]
        public void Save()
        {
            _savedLayout = PlayerPrefs.GetString("rwbr.touchLayout", null);
            GameSettings.Load();
            _savedVibration = GameSettings.Vibration;
        }

        [TearDown]
        public void Restore()
        {
            if (_savedLayout == null) PlayerPrefs.DeleteKey("rwbr.touchLayout");
            else PlayerPrefs.SetString("rwbr.touchLayout", _savedLayout);
            TouchControls.ReloadLayout();
            GameSettings.SetVibration(_savedVibration);
        }

        private static TouchControls.ButtonLayout Find(TouchButton b)
        {
            foreach (TouchControls.ButtonLayout l in TouchControls.Layout)
                if (l.Button == b) return l;
            throw new AssertionException(b.ToString());
        }

        [Test]
        public void Moved_and_resized_buttons_are_saved_and_restored()
        {
            TouchControls.ResetLayout();
            float fireRadius = Find(TouchButton.Fire).Radius;
            TouchControls.MoveButton(TouchButton.Aim, new Vector2(0.6f, 0.5f));
            TouchControls.SetButtonScale(1.2f);
            TouchControls.SaveLayout();

            TouchControls.ReloadLayout(); // as on the next launch
            Assert.AreEqual(new Vector2(0.6f, 0.5f), Find(TouchButton.Aim).Center);
            Assert.AreEqual(1.2f, TouchControls.ButtonScale, 1e-4f);
            Assert.AreEqual(fireRadius * 1.2f, Find(TouchButton.Fire).Radius, 1e-4f);

            TouchControls.ResetLayout();
            TouchControls.ReloadLayout();
            Assert.AreEqual(TouchControls.Defaults[0].Center, Find(TouchControls.Defaults[0].Button).Center);
            Assert.AreEqual(1f, TouchControls.ButtonScale);
        }

        [Test]
        public void Buttons_stay_on_screen_and_overlaps_are_reported()
        {
            TouchControls.ResetLayout();
            CollectionAssert.IsEmpty(TouchControls.Overlaps(16f / 9f));
            TouchControls.MoveButton(TouchButton.Aim, new Vector2(2f, -1f));
            Vector2 c = Find(TouchButton.Aim).Center;
            Assert.IsTrue(c.x <= 0.97f && c.y >= 0.04f, "kept on screen");

            TouchControls.MoveButton(TouchButton.Aim, Find(TouchButton.Fire).Center);
            Assert.IsTrue(TouchControls.Overlaps(16f / 9f).Exists(o => (o.A.Button == TouchButton.Aim || o.B.Button == TouchButton.Aim)));
        }

        [Test]
        public void Broken_saved_layouts_fall_back_to_the_defaults()
        {
            PlayerPrefs.SetString("rwbr.touchLayout", "v1;banana;Fire:x,y;Nope:0.1,0.1");
            TouchControls.ReloadLayout();
            Assert.AreEqual(TouchControls.Defaults.Length, TouchControls.Layout.Length);
            Assert.AreEqual(1f, TouchControls.ButtonScale);
            PlayerPrefs.SetString("rwbr.touchLayout", "v1;0.9;Fire:0.5,0.5;Nope:0.1,0.1");
            TouchControls.ReloadLayout();
            Assert.AreEqual(new Vector2(0.5f, 0.5f), Find(TouchButton.Fire).Center, "valid entries survive next to unknown ones");
            Assert.AreEqual(0.9f, TouchControls.ButtonScale, 1e-4f);
        }

        [Test]
        public void Vibration_can_be_turned_off()
        {
            GameSettings.SetVibration(true);
            int before = Haptics.Pulses;
            Haptics.Hit();
            Haptics.Kill();
            Assert.AreEqual(before + 2, Haptics.Pulses);
            Assert.AreEqual(40, Haptics.LastMilliseconds);

            GameSettings.SetVibration(false);
            Haptics.Damaged();
            Assert.AreEqual(before + 2, Haptics.Pulses, "no pulse with vibration off");
        }
    }
}
