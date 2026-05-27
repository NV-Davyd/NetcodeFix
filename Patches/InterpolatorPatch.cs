using System;
using System.Reflection;
using HarmonyLib;
using KSL.API;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Patches
{
    // Sanity-filters incoming PosDataFrames before they're stored in the
    // circular interpolator buffer.
    //
    // Symptom this fixes: a remote car suddenly teleports 30m through a wall
    // and the smoothing system then drags it back over a couple of seconds.
    // The bad frame came from a corrupted packet, a wildly mis-stamped time
    // sync, or a genuine 1-second lag spike. The interpolator has no idea
    // it's bad and happily snaps to it.
    //
    // Our filter: compare the new frame's position against the most recent
    // frame in the buffer. If the implied speed (Δpos / Δt) exceeds a sane
    // threshold (default 80 m/s ≈ 288 km/h), drop the frame. The next valid
    // packet will land correctly.
    //
    // We skip the filter on the very first frame (no prior frame to compare
    // against) and after generation changes (respawn — the position
    // legitimately teleports).
    internal static class InterpolatorPatch
    {
        // Cached reflection — these private fields are hit on every incoming
        // packet of every remote player, so avoid Traverse's per-call cost.
        private static readonly FieldInfo F_data    = AccessTools.Field(typeof(NetGameCircularInterpolator), "m_data");
        private static readonly FieldInfo F_current = AccessTools.Field(typeof(NetGameCircularInterpolator), "m_current");
        private static readonly FieldInfo F_count   = AccessTools.Field(typeof(NetGameCircularInterpolator), "m_count");

        public static bool AddDataPrefix(NetGameCircularInterpolator __instance, ref PosDataFrame dataFrame)
        {
            if (!Settings.Current.enabled) return true;
            if (F_data == null || F_current == null || F_count == null) return true;

            try
            {
                int count = (int)F_count.GetValue(__instance);
                if (count == 0) return true; // first frame, nothing to compare

                int current = (int)F_current.GetValue(__instance);
                var buffer  = (PosDataFrame[])F_data.GetValue(__instance);
                if (buffer == null) return true;

                ref var last = ref buffer[current];

                // Generation change = legitimate respawn / scene transition.
                // Don't filter those.
                if (last.posGeneration != dataFrame.posGeneration) return true;

                long dtMs = dataFrame.modelTime - last.modelTime;
                if (dtMs <= 0) return true; // out-of-order packet, let game's own logic handle

                float dt = dtMs / 1000f;
                Vector3 deltaPos = dataFrame.position - last.position;
                float impliedSpeed = deltaPos.magnitude / dt;

                if (impliedSpeed > Settings.Current.maxImpliedSpeedMps)
                {
                    Settings.Current.rejectedFramesThisSession++;
                    // Don't add the bad frame; let the interpolator continue
                    // extrapolating from the last good frame until a sane
                    // packet arrives. Better than snapping to garbage.
                    return false;
                }
            }
            catch (Exception ex)
            {
                // If anything goes sideways with reflection, fall back to
                // vanilla behavior — never block the original method on
                // our filter's errors.
                Kino.Log.Warning("[NetcodeFix] Sanity filter error: " + ex.Message);
            }

            return true;
        }
    }
}
