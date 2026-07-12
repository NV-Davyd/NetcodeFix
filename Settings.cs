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

        public static void Load()
        {
            Kino.Prefs.TryGet(Prefix + "correction_rate", out CorrectionRate, DefaultCorrectionRate);
            Clamp();
        }

        public static void Save()
        {
            Clamp();
            Kino.Prefs.Set(Prefix + "correction_rate", CorrectionRate);
        }

        private static void Clamp()
        {
            CorrectionRate = Mathf.Clamp(CorrectionRate, MinCorrectionRate, MaxCorrectionRate);
        }
    }
}
