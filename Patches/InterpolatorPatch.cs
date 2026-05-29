using System;
using System.Reflection;
using HarmonyLib;
using NetcodeFix.Net;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Patches
{
    internal static class InterpolatorPatch
    {
        private static readonly FieldInfo F_host = AccessTools.Field(typeof(NetGameCircularInterpolator), "m_host");
        private static readonly FieldInfo F_lastExtrapolated =
            AccessTools.Field(typeof(NetGameCircularInterpolator), "<lastExtrapolated>k__BackingField");

        public static bool GetDataFramePrefix(NetGameCircularInterpolator __instance, long modelTime, ref PosDataFrame __result)
        {
            if (!Settings.Enabled || !Settings.SharePosePackets) return true;

            var host = F_host?.GetValue(__instance) as NetworkPlayer;
            if (host == null || host.IsLocal) return true;

            long nativeLatestTime = __instance.GetLast().modelTime;
            if (!SharedPoseSync.TrySample(host.NetworkID, modelTime, nativeLatestTime, out __result))
            {
                return true;
            }

            F_lastExtrapolated?.SetValue(__instance, false);
            return false;
        }

        public static bool InterpolatePrefix(long modelTime, ref PosDataFrame left, ref PosDataFrame right, ref PosDataFrame __result)
        {
            if (!Settings.Enabled || !Settings.FixFractionalInterpolation) return true;

            long span = right.modelTime - left.modelTime;
            float t = span <= 0L ? 0f : Mathf.Clamp01((float)(modelTime - left.modelTime) / span);

            __result = PosDataFrame.Default;
            __result.modelTime = Math.Min(Math.Max(modelTime, left.modelTime), right.modelTime);
            __result.posGeneration = left.posGeneration;
            __result.flags = left.flags;
            __result.accelerate = Mathf.Lerp(left.accelerate, right.accelerate, t);
            __result.gear = t < 0.5f ? left.gear : right.gear;
            __result.brake = Mathf.Lerp(left.brake, right.brake, t);
            __result.handbrake = Mathf.Lerp(left.handbrake, right.handbrake, t);
            __result.flares = t < 0.5f ? left.flares : right.flares;
            __result.steer = Mathf.Lerp(left.steer, right.steer, t);
            __result.clutch = Mathf.Lerp(left.clutch, right.clutch, t);
            __result.autoClutch = t < 0.5f ? left.autoClutch : right.autoClutch;
            __result.clutchKick = t < 0.5f ? left.clutchKick : right.clutchKick;
            __result.rpm = Mathf.Lerp(left.rpm, right.rpm, t);
            __result.mileage = Mathf.Lerp(left.mileage, right.mileage, t);
            __result.velocity = Vector3.Lerp(left.velocity, right.velocity, t);
            __result.angularVelocity = Vector3.Lerp(left.angularVelocity, right.angularVelocity, t);
            __result.position = Vector3.Lerp(left.position, right.position, t);
            __result.rotation = Quaternion.Lerp(left.rotation, right.rotation, t);
            __result.angAccel = Mathf.Lerp(left.angAccel, right.angAccel, t);
            __result.longAccel = Mathf.Lerp(left.longAccel, right.longAccel, t);
            __result.sideAccel = Mathf.Lerp(left.sideAccel, right.sideAccel, t);
            __result.processEngineStart = t < 0.5f ? left.processEngineStart : right.processEngineStart;
            __result.ignitionEnabled = t < 0.5f ? left.ignitionEnabled : right.ignitionEnabled;
            __result.starterEnabled = t < 0.5f ? left.starterEnabled : right.starterEnabled;
            __result.engineStartTime = Mathf.Lerp(left.engineStartTime, right.engineStartTime, t);
            return false;
        }
    }
}
