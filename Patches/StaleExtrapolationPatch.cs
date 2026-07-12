using System;
using System.Reflection;
using HarmonyLib;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Patches
{
    internal static class StaleExtrapolationPatch
    {
        private const long StockPredictionLimitMs = 300L;
        private const long CoastPredictionLimitMs = 1000L;
        private const float PredictionStepSecs = 0.02f;
        private const float MaxLinearAcceleration = 35f;

        private delegate void ExtrapolateDelegate(
            NetGameCircularInterpolator instance,
            ref PosDataFrame extrapolated,
            in PosDataFrame last,
            float dt);

        private static readonly AccessTools.FieldRef<NetGameCircularInterpolator, PosDataFrame[]> Data =
            AccessTools.FieldRefAccess<NetGameCircularInterpolator, PosDataFrame[]>("m_data");

        private static readonly AccessTools.FieldRef<NetGameCircularInterpolator, int> Current =
            AccessTools.FieldRefAccess<NetGameCircularInterpolator, int>("m_current");

        private static readonly ExtrapolateDelegate Extrapolate = CreateExtrapolateDelegate();

        public static bool ExtrapolateLastPrefix(
            NetGameCircularInterpolator __instance,
            long modelTime,
            ref PosDataFrame __result)
        {
            PosDataFrame[] frames = Data(__instance);
            int current = Current(__instance);
            if (frames == null || current < 0 || current >= frames.Length)
            {
                return true;
            }

            PosDataFrame last = frames[current];
            long ageMs = modelTime - last.modelTime;
            if (ageMs <= StockPredictionLimitMs || ageMs > CoastPredictionLimitMs || !last.IsValid())
            {
                return true;
            }

            // Start from exactly the same 300 ms prediction as the normal path. Calling the
            // patched Extrapolate method also retains InterpolatorPatch's enhanced prediction.
            PosDataFrame predicted = PosDataFrame.Default;
            predicted.modelTime = modelTime;
            Extrapolate(__instance, ref predicted, in last, StockPredictionLimitMs / 1000f);
            if (!IsFinite(predicted.position) ||
                !IsFinite(predicted.velocity) ||
                !IsFinite(predicted.angularVelocity) ||
                !IsFinite(predicted.rotation))
            {
                return true;
            }

            float coastSecs = (ageMs - StockPredictionLimitMs) / 1000f;
            float fadeWindowSecs = (CoastPredictionLimitMs - StockPredictionLimitMs) / 1000f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(coastSecs / PredictionStepSecs));
            float stepDt = coastSecs / steps;

            Vector3 position = predicted.position;
            Vector3 velocity = predicted.velocity;
            Vector3 angularVelocity = predicted.angularVelocity;
            Quaternion rotation = predicted.rotation;
            InterpolatorPatch.ResolvePredictionInputs(
                __instance,
                in last,
                StockPredictionLimitMs / 1000f,
                out Vector3 localAcceleration,
                out float angularAcceleration,
                out _);

            for (int i = 0; i < steps; i++)
            {
                float elapsedAtMidpoint = (i + 0.5f) * stepDt;
                float influence = 1f - Mathf.Clamp01(elapsedAtMidpoint / fadeWindowSecs);
                Vector3 worldAcceleration =
                    Vector3.ClampMagnitude(rotation * localAcceleration, MaxLinearAcceleration) * influence;

                position += velocity * stepDt + worldAcceleration * (0.5f * stepDt * stepDt);
                velocity += worldAcceleration * stepDt;

                Vector3 angularAccelerationVector = rotation * Vector3.up * angularAcceleration * influence;
                Vector3 midpointAngularVelocity = angularVelocity + angularAccelerationVector * (0.5f * stepDt);
                float angularSpeed = midpointAngularVelocity.magnitude;
                if (angularSpeed > Mathf.Epsilon)
                {
                    rotation = Quaternion.AngleAxis(
                        angularSpeed * Mathf.Rad2Deg * stepDt,
                        midpointAngularVelocity / angularSpeed) * rotation;
                }

                angularVelocity += angularAccelerationVector * stepDt;
            }

            if (!IsFinite(position) ||
                !IsFinite(velocity) ||
                !IsFinite(angularVelocity) ||
                !IsFinite(rotation))
            {
                return true;
            }

            predicted.modelTime = modelTime;
            predicted.position = position;
            predicted.velocity = velocity;
            predicted.angularVelocity = angularVelocity;
            predicted.rotation = rotation;
            __result = predicted;
            return false;
        }

        private static ExtrapolateDelegate CreateExtrapolateDelegate()
        {
            MethodInfo method = AccessTools.Method(typeof(NetGameCircularInterpolator), "Extrapolate");
            return (ExtrapolateDelegate)Delegate.CreateDelegate(typeof(ExtrapolateDelegate), null, method);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
