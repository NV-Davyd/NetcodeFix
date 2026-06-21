using KSL.API;
using UnityEngine;

namespace NetcodeFix
{
    public static class Settings
    {
        private const string Prefix = "netcodefix_";
        private const int CurrentSettingsVersion = 3;

        public const float NativeSendRateHz = 60f;
        public const float MinCorrectionRate = 0.8f;
        public const float MaxCorrectionRate = 2.5f;
        public const float DefaultCorrectionRate = 1.0f;
        public static float CorrectionRate = DefaultCorrectionRate;
        public static bool ShowDefaultResetNotice;

        public static void Load()
        {
            bool hasSettingsVersion = Kino.Prefs.TryGet(Prefix + "settings_version", out int settingsVersion, 0);
            bool hasCorrectionRate = Kino.Prefs.TryGet(Prefix + "correction_rate", out float savedCorrectionRate, DefaultCorrectionRate);
            bool hasNearCorrectionRate = Kino.Prefs.TryGet(Prefix + "near_correction_rate", out float savedNearCorrectionRate, DefaultCorrectionRate);
            if (!hasSettingsVersion || settingsVersion < CurrentSettingsVersion)
            {
                CorrectionRate = DefaultCorrectionRate;
                ShowDefaultResetNotice = hasSettingsVersion || hasCorrectionRate || hasNearCorrectionRate;
            }
            else if (!hasCorrectionRate)
            {
                CorrectionRate = hasNearCorrectionRate ? savedNearCorrectionRate : DefaultCorrectionRate;
                Kino.Prefs.TryGet(Prefix + "show_default_reset_notice", out ShowDefaultResetNotice, false);
            }
            else
            {
                CorrectionRate = savedCorrectionRate;
                Kino.Prefs.TryGet(Prefix + "show_default_reset_notice", out ShowDefaultResetNotice, false);
            }

            Clamp();
            Save();
        }

        public static void Save()
        {
            Clamp();
            Kino.Prefs.Set(Prefix + "enabled", true);
            Kino.Prefs.Set(Prefix + "override_native_send_rate", true);
            Kino.Prefs.Set(Prefix + "native_send_rate_hz", NativeSendRateHz);
            Kino.Prefs.Set(Prefix + "share_pose_packets", false);
            Kino.Prefs.Set(Prefix + "fix_fractional_interpolation", true);
            Kino.Prefs.Set(Prefix + "correction_rate", CorrectionRate);
            Kino.Prefs.Set(Prefix + "show_default_reset_notice", ShowDefaultResetNotice);
            Kino.Prefs.Set(Prefix + "settings_version", CurrentSettingsVersion);
        }

        private static void Clamp()
        {
            CorrectionRate = Mathf.Clamp(CorrectionRate, MinCorrectionRate, MaxCorrectionRate);
        }
    }
}
