using KSL.API;
using UnityEngine;

namespace NetcodeFix
{
    public static class Settings
    {
        private const string Prefix = "netcodefix_";

        public const float NativeSendRateHz = 60f;
        public const float MinCorrectionRate = 0.8f;
        public const float MaxCorrectionRate = 2.5f;
        public const float DefaultCorrectionRate = 1.0f;
        public static float CorrectionRate = DefaultCorrectionRate;
        public static bool ManualCorrectionTuning;
        public static bool DrawSyncOverlay;

        public static float EffectiveCorrectionRate =>
            ManualCorrectionTuning ? CorrectionRate : DefaultCorrectionRate;

        public static void Load()
        {
            Kino.Prefs.TryGet(Prefix + "correction_rate", out CorrectionRate, DefaultCorrectionRate);
            Kino.Prefs.TryGet(Prefix + "manual_correction_tuning", out ManualCorrectionTuning, false);
            Kino.Prefs.TryGet(Prefix + "draw_sync_overlay", out DrawSyncOverlay, false);
            Clamp();
        }

        public static void Save()
        {
            Clamp();
            Kino.Prefs.Set(Prefix + "correction_rate", CorrectionRate);
            Kino.Prefs.Set(Prefix + "manual_correction_tuning", ManualCorrectionTuning);
            Kino.Prefs.Set(Prefix + "draw_sync_overlay", DrawSyncOverlay);
        }

        private static void Clamp()
        {
            CorrectionRate = Mathf.Clamp(CorrectionRate, MinCorrectionRate, MaxCorrectionRate);
        }
    }
}
