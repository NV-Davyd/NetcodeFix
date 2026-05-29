using KSL.API;
using UnityEngine;

namespace NetcodeFix
{
    public static class Settings
    {
        private const string Prefix = "netcodefix_";

        public const float NativeSendRateHz = 60f;
        public static float CorrectionRate = 2.5f;

        public static void Load()
        {
            if (!Kino.Prefs.TryGet(Prefix + "correction_rate", out CorrectionRate, 2.5f))
            {
                Kino.Prefs.TryGet(Prefix + "near_correction_rate", out CorrectionRate, 2.5f);
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
        }

        private static void Clamp()
        {
            CorrectionRate = Mathf.Clamp(CorrectionRate, 0.5f, 6f);
        }
    }
}
