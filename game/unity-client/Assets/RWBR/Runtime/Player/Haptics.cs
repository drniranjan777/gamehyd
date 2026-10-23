using RWBR.Client.Settings;
using UnityEngine;

namespace RWBR.Client.Player
{
    /// <summary>
    /// Short vibrations on phones: a tick when your shot hits, a stronger pulse for an elimination, and one when you take
    /// damage. Android uses the system vibrator with a one-shot effect (Android 8+, plain duration before that). iOS has no
    /// short vibration without a native plugin, so there only eliminations and big hits use the standard vibration.
    /// Off with Settings > Accessibility > Vibration. Nothing happens in the editor or on desktop.
    /// </summary>
    public static class Haptics
    {
        /// <summary>Pulses requested since start (tests and diagnostics; counted even where the device can't vibrate).</summary>
        public static int Pulses { get; private set; }
        public static int LastMilliseconds { get; private set; }

        public static void Hit() => Pulse(12, 90);
        public static void Kill() => Pulse(40, 220);
        public static void Damaged() => Pulse(28, 160);

        public static void Pulse(int milliseconds, int amplitude)
        {
            if (!GameSettings.Vibration) return;
            Pulses++;
            LastMilliseconds = milliseconds;
#if UNITY_ANDROID && !UNITY_EDITOR
            Android(milliseconds, Mathf.Clamp(amplitude, 1, 255));
#elif UNITY_IOS && !UNITY_EDITOR
            if (milliseconds >= 40) Handheld.Vibrate();
#endif
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
