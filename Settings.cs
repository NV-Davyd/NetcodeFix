using KSL.API;
using UnityEngine;

namespace NetcodeFix
{
    public static class Settings
    {
        private const string Prefix = "netcodefix_";

        public static bool Enabled = true;
        public static bool OverrideNativeSendRate = true;
        public static float NativeSendRateHz = 50f;
        public static bool SharePosePackets = false;
        public static bool FixFractionalInterpolation = true;
        public static float CorrectionRate = 2.5f;

        public static void Load()
        {
            Kino.Prefs.TryGet(Prefix + "enabled", out Enabled, true);
            Kino.Prefs.TryGet(Prefix + "override_native_send_rate", out OverrideNativeSendRate, true);
            Kino.Prefs.TryGet(Prefix + "native_send_rate_hz", out NativeSendRateHz, 50f);
            Kino.Prefs.TryGet(Prefix + "share_pose_packets", out SharePosePackets, false);
            Kino.Prefs.TryGet(Prefix + "fix_fractional_interpolation", out FixFractionalInterpolation, true);
            if (!Kino.Prefs.TryGet(Prefix + "correction_rate", out CorrectionRate, 2.5f))
            {
                Kino.Prefs.TryGet(Prefix + "near_correction_rate", out CorrectionRate, 2.5f);
            }
            Clamp();
        }

        public static void Save()
        {
            Clamp();
            Kino.Prefs.Set(Prefix + "enabled", Enabled);
            Kino.Prefs.Set(Prefix + "override_native_send_rate", OverrideNativeSendRate);
            Kino.Prefs.Set(Prefix + "native_send_rate_hz", NativeSendRateHz);
            Kino.Prefs.Set(Prefix + "share_pose_packets", SharePosePackets);
            Kino.Prefs.Set(Prefix + "fix_fractional_interpolation", FixFractionalInterpolation);
            Kino.Prefs.Set(Prefix + "correction_rate", CorrectionRate);
        }

        private static void Clamp()
        {
            NativeSendRateHz = Mathf.Clamp(NativeSendRateHz, 30f, 60f);
            CorrectionRate = Mathf.Clamp(CorrectionRate, 0.5f, 6f);
        }
    }
}
