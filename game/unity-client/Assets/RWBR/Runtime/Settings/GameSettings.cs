using UnityEngine;

namespace RWBR.Client.Settings
{
    public enum QualityPreset
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }

    /// <summary>Player settings persisted with PlayerPrefs. Every read/write tolerates missing storage.</summary>
    public static class GameSettings
    {
        private const string SensitivityKey = "rwbr.sensitivity";
        private const string QualityKey = "rwbr.quality";
        private const string ShowPerfKey = "rwbr.showPerf";
        private const string MasterKey = "rwbr.volume.master";
        private const string SfxKey = "rwbr.volume.sfx";
        private const string UiKey = "rwbr.volume.ui";

        public const float MinSensitivity = 0.2f;
        public const float MaxSensitivity = 3f;

        public static float Sensitivity { get; private set; } = 1f;
        public static QualityPreset Quality { get; private set; } = QualityPreset.Medium;
        public static bool ShowPerformance { get; private set; } = true;
        public static float MasterVolume { get; private set; } = 0.8f;
        public static float SfxVolume { get; private set; } = 1f;
        public static float UiVolume { get; private set; } = 0.8f;

        public static void Load()
        {
            try
            {
                Sensitivity = Mathf.Clamp(PlayerPrefs.GetFloat(SensitivityKey, 1f), MinSensitivity, MaxSensitivity);
                Quality = (QualityPreset)Mathf.Clamp(PlayerPrefs.GetInt(QualityKey, DefaultQuality()), 0, 2);
                ShowPerformance = PlayerPrefs.GetInt(ShowPerfKey, 1) == 1;
                MasterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(MasterKey, 0.8f));
                SfxVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(SfxKey, 1f));
                UiVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(UiKey, 0.8f));
            }
            catch (System.Exception)
            {
                // Storage unavailable: keep defaults.
            }
            ApplyQuality(Quality);
        }

        public static void SetSensitivity(float value)
        {
            Sensitivity = Mathf.Clamp(value, MinSensitivity, MaxSensitivity);
            Save();
        }

        public static void SetQuality(QualityPreset preset)
        {
            Quality = preset;
            ApplyQuality(preset);
            Save();
        }

        public static void SetVolumes(float master, float sfx, float ui)
        {
            MasterVolume = Mathf.Clamp01(master);
            SfxVolume = Mathf.Clamp01(sfx);
            UiVolume = Mathf.Clamp01(ui);
            Save();
        }

        public static void SetShowPerformance(bool show)
        {
            ShowPerformance = show;
            Save();
        }

        /// <summary>Low on devices under 4 GB RAM, High on 8 GB+, Medium otherwise. Re-tuned once the device matrix is measured.</summary>
        public static int DefaultQuality()
        {
            int mb = SystemInfo.systemMemorySize;
            if (mb > 0 && mb < 4000) return (int)QualityPreset.Low;
            if (mb >= 7500) return (int)QualityPreset.High;
            return (int)QualityPreset.Medium;
        }

        public static void ApplyQuality(QualityPreset preset)
        {
            QualitySettings.vSyncCount = 0;
            switch (preset)
            {
                case QualityPreset.Low:
                    QualitySettings.shadows = ShadowQuality.Disable;
                    QualitySettings.shadowDistance = 0;
                    QualitySettings.antiAliasing = 0;
                    QualitySettings.lodBias = 0.5f;
                    QualitySettings.pixelLightCount = 0;
                    QualitySettings.globalTextureMipmapLimit = 1;
                    Application.targetFrameRate = 30;
                    break;
                case QualityPreset.Medium:
                    QualitySettings.shadows = ShadowQuality.HardOnly;
                    QualitySettings.shadowDistance = 35;
                    QualitySettings.antiAliasing = 0;
                    QualitySettings.lodBias = 1f;
                    QualitySettings.pixelLightCount = 1;
                    QualitySettings.globalTextureMipmapLimit = 0;
                    Application.targetFrameRate = 30;
                    break;
                default:
                    QualitySettings.shadows = ShadowQuality.All;
                    QualitySettings.shadowDistance = 70;
                    QualitySettings.antiAliasing = 4;
                    QualitySettings.lodBias = 1.5f;
                    QualitySettings.pixelLightCount = 2;
                    QualitySettings.globalTextureMipmapLimit = 0;
                    Application.targetFrameRate = 60;
                    break;
            }
        }

        private static void Save()
        {
            try
            {
                PlayerPrefs.SetFloat(SensitivityKey, Sensitivity);
                PlayerPrefs.SetInt(QualityKey, (int)Quality);
                PlayerPrefs.SetInt(ShowPerfKey, ShowPerformance ? 1 : 0);
                PlayerPrefs.SetFloat(MasterKey, MasterVolume);
                PlayerPrefs.SetFloat(SfxKey, SfxVolume);
                PlayerPrefs.SetFloat(UiKey, UiVolume);
                PlayerPrefs.Save();
            }
            catch (System.Exception)
            {
                // Storage unavailable: settings last for this session only.
            }
        }
    }
}
