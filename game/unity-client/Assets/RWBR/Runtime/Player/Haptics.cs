using RWBR.Client.Settings;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RWBR.Client.Player
{
    /// <summary>
    /// Short vibrations on phones: a tick when your shot hits, a stronger pulse for an elimination, and one when you take
    /// damage. Android uses the system vibrator with a one-shot effect (Android 8+, plain duration before that). iOS has no
    /// short vibration without a native plugin, so there only eliminations and big hits use the standard vibration.
    /// A connected gamepad rumbles for the same events, on any platform (the pulse is stretched to at least 80 ms, since
    /// shorter rumbles can't be felt). Off with Settings > Accessibility > Vibration.
    /// </summary>
    public static class Haptics
    {
        /// <summary>Pulses requested since start (tests and diagnostics; counted even where the device can't vibrate).</summary>
        public static int Pulses { get; private set; }
        public static int LastMilliseconds { get; private set; }
        /// <summary>Motor speeds of the last gamepad rumble (low-frequency, high-frequency), 0..1.</summary>
        public static Vector2 LastRumble { get; private set; }
        /// <summary>Gamepad rumbles started since start (tests and diagnostics).</summary>
        public static int Rumbles { get; private set; }

        /// <summary>Shortest gamepad rumble; motors need time to spin up.</summary>
        public const int MinRumbleMilliseconds = 80;

        public static void Hit() => Pulse(12, 90);
        public static void Kill() => Pulse(40, 220);
        public static void Damaged() => Pulse(28, 160);

        public static void Pulse(int milliseconds, int amplitude)
        {
            if (!GameSettings.Vibration) return;
            Pulses++;
            LastMilliseconds = milliseconds;
            Rumble(Gamepad.current, milliseconds, amplitude);
#if UNITY_ANDROID && !UNITY_EDITOR
            Android(milliseconds, Mathf.Clamp(amplitude, 1, 255));
#elif UNITY_IOS && !UNITY_EDITOR
            if (milliseconds >= 40) Handheld.Vibrate();
#endif
        }

        /// <summary>Rumbles <paramref name="pad"/> (if any) and stops it after the pulse, stretched to the minimum.</summary>
        public static void Rumble(Gamepad pad, int milliseconds, int amplitude)
        {
            if (pad == null) return;
            float strength = Mathf.Clamp(amplitude, 1, 255) / 255f;
            LastRumble = new Vector2(strength * 0.6f, strength); // the heavy motor softer, the light motor at full
            Rumbles++;
            try
            {
                pad.SetMotorSpeeds(LastRumble.x, LastRumble.y);
            }
            catch (System.Exception)
            {
                return; // pads without motors
            }
            if (Application.isPlaying) RumbleStopper.StopAfter(pad, Mathf.Max(milliseconds, MinRumbleMilliseconds) / 1000f);
        }

        /// <summary>Stops a pad's motors now (pause, leaving a match, tests).</summary>
        public static void StopRumble(Gamepad pad)
        {
            if (pad == null) return;
            try
            {
                pad.ResetHaptics();
            }
            catch (System.Exception)
            {
                // Pad removed.
            }
        }

        /// <summary>Stops the motors when the pulse ends, and whenever the app loses focus.</summary>
        private sealed class RumbleStopper : MonoBehaviour
        {
            private static RumbleStopper _instance;
            private Gamepad _pad;
            private float _until;

            public static void StopAfter(Gamepad pad, float seconds)
            {
                if (_instance == null)
                {
                    var go = new GameObject("RWBR Rumble") { hideFlags = HideFlags.HideAndDontSave };
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<RumbleStopper>();
                }
                if (_instance._pad != null && _instance._pad != pad) StopRumble(_instance._pad);
                _instance._pad = pad;
                _instance._until = Time.unscaledTime + seconds;
                _instance.enabled = true;
            }

            private void Update()
            {
                if (Time.unscaledTime < _until) return;
                Stop();
            }

            private void OnApplicationFocus(bool focused)
            {
                if (!focused) Stop();
            }

            private void OnDisable() => StopRumble(_pad);

            private void Stop()
            {
                StopRumble(_pad);
                _pad = null;
                enabled = false;
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject _vibrator;
        private static bool _failed;

        private static void Android(int milliseconds, int amplitude)
        {
            if (_failed) return;
            try
            {
                if (_vibrator == null)
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    _vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }
                using var version = new AndroidJavaClass("android.os.Build$VERSION");
                if (version.GetStatic<int>("SDK_INT") >= 26)
                {
                    using var effects = new AndroidJavaClass("android.os.VibrationEffect");
                    using AndroidJavaObject effect = effects.CallStatic<AndroidJavaObject>("createOneShot", (long)milliseconds, amplitude);
                    _vibrator.Call("vibrate", effect);
                }
                else
                {
                    _vibrator.Call("vibrate", (long)milliseconds);
                }
            }
            catch (System.Exception)
            {
                // No vibrator or no permission: fall back once to Unity's (longer) vibration for eliminations.
                _failed = true;
                if (milliseconds >= 40) Handheld.Vibrate(); // this reference also makes Unity add the VIBRATE permission
            }
        }
#endif
    }
}
