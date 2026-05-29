using System.Collections.Generic;
using KSL.API;
using NetcodeFix.Net;
using NetcodeFix.Patches;

namespace NetcodeFix
{
    public static class SettingsUI
    {
        public static void Draw()
        {
            bool enabled = Settings.Enabled;
            if (Kino.UI.Toggle("Enable NetcodeFix", ref enabled))
            {
                Settings.Enabled = enabled;
                Settings.Save();
            }

            Kino.UI.HorizontalLine();
            Kino.UI.GroupLabel("Native sync");

            bool overrideNativeSendRate = Settings.OverrideNativeSendRate;
            if (Kino.UI.Toggle("Patch native send rate", ref overrideNativeSendRate))
            {
                Settings.OverrideNativeSendRate = overrideNativeSendRate;
                Settings.Save();
            }

            float nativeSendRateHz = Settings.NativeSendRateHz;
            if (Kino.UI.Slider(ref nativeSendRateHz, 30f, 60f, $"Native send rate: {Settings.NativeSendRateHz:F0} Hz"))
            {
                Settings.NativeSendRateHz = nativeSendRateHz;
                Settings.Save();
            }

            Kino.UI.Label(NativeSyncRatePatch.StatusLine);
            Kino.UI.Label(NativeSyncRatePatch.RateLine);

            Kino.UI.HorizontalLine();
            Kino.UI.GroupLabel("Shared poses");

            bool sharePosePackets = Settings.SharePosePackets;
            if (Kino.UI.Toggle("Share extra pose packets", ref sharePosePackets))
            {
                Settings.SharePosePackets = sharePosePackets;
                Settings.Save();
            }

            if (!Settings.SharePosePackets)
            {
                Kino.UI.Label("Shared pose diagnostics are hidden while disabled.");
                DrawRemoteSamplingAndCorrection();
                return;
            }

            Kino.UI.Label("Peers: " + SharedPoseSync.ModdedPeerCount
                + "  Sent: " + SharedPoseSync.FramesSent
                + "  Burst: " + SharedPoseSync.BurstFramesSent
                + "  Recv: " + SharedPoseSync.FramesReceived);

            Kino.UI.Label("Used: " + SharedPoseSync.FramesUsed
                + "  Interp: " + SharedPoseSync.SamplesInterpolated
                + "  Predict: " + SharedPoseSync.SamplesExtrapolated);

            Kino.UI.Label("Age native/shared: " + SharedPoseSync.LastNativeAgeMs
                + " / " + SharedPoseSync.LastSharedAgeMs
                + " ms  Predict: " + SharedPoseSync.LastSharedExtrapolateMs + " ms");

            Kino.UI.Label("Clock offset: " + SharedPoseSync.LastTimelineOffsetMs + " ms");

            Kino.UI.Label("Last peer map: " + SharedPoseSync.LastPeerMap);

            Kino.UI.Label("Fallback no peer/stale/no sample: " + SharedPoseSync.FallbackNoBuffer
                + " / " + SharedPoseSync.FallbackStale
                + " / " + SharedPoseSync.FallbackNoSample);

            Kino.UI.Label(SharedPoseSync.RateLine);
            Kino.UI.Label(SharedPoseSync.QualityLine);

            Kino.UI.GroupLabel("Active peer");
            if (!DrawFirstLine(SharedPoseSync.GetActiveDebugLines(1)))
            {
                Kino.UI.Label("No mapped modded peer yet.");
            }

            DrawFirstLine(SharedPoseSync.GetActiveQualityDebugLines(1));

            Kino.UI.GroupLabel("Peer debug");
            if (!DrawLines(SharedPoseSync.GetActiveDebugLines(4)))
            {
                Kino.UI.Label("No NetcodeFix peer packets received.");
            }

            Kino.UI.GroupLabel("Peer quality");
            if (!DrawLines(SharedPoseSync.GetActiveQualityDebugLines(4)))
            {
                Kino.UI.Label("No NetcodeFix peer quality yet.");
            }

            DrawRemoteSamplingAndCorrection();
        }

        private static void DrawRemoteSamplingAndCorrection()
        {
            Kino.UI.HorizontalLine();
            Kino.UI.GroupLabel("Remote sampling");

            bool fixInterpolation = Settings.FixFractionalInterpolation;
            if (Kino.UI.Toggle("Fix fractional interpolation", ref fixInterpolation))
            {
                Settings.FixFractionalInterpolation = fixInterpolation;
                Settings.Save();
            }

            Kino.UI.HorizontalLine();
            Kino.UI.GroupLabel("Correction");

            float correctionRate = Settings.CorrectionRate;
            if (Kino.UI.Slider(ref correctionRate, 0.5f, 6f, $"Correction rate: {Settings.CorrectionRate:F2}"))
            {
                Settings.CorrectionRate = correctionRate;
                Settings.Save();
            }
        }

        private static bool DrawFirstLine(IEnumerable<string> lines)
        {
            foreach (string line in lines)
            {
                Kino.UI.Label(line);
                return true;
            }

            return false;
        }

        private static bool DrawLines(IEnumerable<string> lines)
        {
            bool drewAny = false;
            foreach (string line in lines)
            {
                Kino.UI.Label(line);
                drewAny = true;
            }

            return drewAny;
        }
    }
}
