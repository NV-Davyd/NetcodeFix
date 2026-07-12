using System;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Patches
{
    internal static class InterpolatorPatch
    {
        private const float EnhancedPredictionStartSecs = 0.04f;
        private const float EnhancedPredictionFullSecs = 0.12f;
        private const float PredictionStepSecs = 0.02f;
        private const float MaxLinearAcceleration = 35f;
        private const float MaxAngularAcceleration = 20f;
        private const float MillisecondsToSeconds = 0.001f;
        private const float MinHermiteTangentLimit = 0.05f;
        private const float MaxHermiteTangentToChord = 2f;
        private const float MinHermiteDeviationLimit = 0.02f;
        private const float MaxHermiteDeviationToChord = 0.5f;

        public static bool InterpolatePrefix(long modelTime, ref PosDataFrame left, ref PosDataFrame right, ref PosDataFrame __result)
        {
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
            __result.position = InterpolatePosition(ref left, ref right, span, t);
            __result.rotation = Quaternion.Slerp(left.rotation, right.rotation, t);
            __result.angAccel = Mathf.Lerp(left.angAccel, right.angAccel, t);
            __result.longAccel = Mathf.Lerp(left.longAccel, right.longAccel, t);
            __result.sideAccel = Mathf.Lerp(left.sideAccel, right.sideAccel, t);
            __result.processEngineStart = t < 0.5f ? left.processEngineStart : right.processEngineStart;
            __result.ignitionEnabled = t < 0.5f ? left.ignitionEnabled : right.ignitionEnabled;
            __result.starterEnabled = t < 0.5f ? left.starterEnabled : right.starterEnabled;
            __result.engineStartTime = Mathf.Lerp(left.engineStartTime, right.engineStartTime, t);
            return false;
        }

        private static Vector3 InterpolatePosition(ref PosDataFrame left, ref PosDataFrame right, long spanMs, float t)
        {
            Vector3 linearPosition = Vector3.Lerp(left.position, right.position, t);
            if (spanMs <= 0L || left.posGeneration != right.posGeneration ||
                !IsFinite(left.position) || !IsFinite(right.position) ||
                !IsFinite(left.velocity) || !IsFinite(right.velocity))
            {
                return linearPosition;
            }

            float spanSecs = spanMs * MillisecondsToSeconds;
            if (!IsFinite(spanSecs) || spanSecs <= 0f)
            {
                return linearPosition;
            }

            Vector3 chord = right.position - left.position;
            float chordLength = chord.magnitude;
            float tangentLimit = Mathf.Max(MinHermiteTangentLimit, chordLength * MaxHermiteTangentToChord);
            Vector3 leftTangent = Vector3.ClampMagnitude(left.velocity * spanSecs, tangentLimit);
            Vector3 rightTangent = Vector3.ClampMagnitude(right.velocity * spanSecs, tangentLimit);

            float t2 = t * t;
            float t3 = t2 * t;
            Vector3 hermitePosition =
                (2f * t3 - 3f * t2 + 1f) * left.position +
                (t3 - 2f * t2 + t) * leftTangent +
                (-2f * t3 + 3f * t2) * right.position +
                (t3 - t2) * rightTangent;
            if (!IsFinite(hermitePosition))
            {
                return linearPosition;
            }

            // Bad or stale endpoint velocities can make a cubic loop far away from both samples.
            // Preserve normal curvature, but bound that curve around the trusted linear segment.
            float deviationLimit = Mathf.Max(MinHermiteDeviationLimit, chordLength * MaxHermiteDeviationToChord);
            return linearPosition + Vector3.ClampMagnitude(hermitePosition - linearPosition, deviationLimit);
        }

        public static void ExtrapolatePostfix(
            NetGameCircularInterpolator __instance,
            ref PosDataFrame extrapolated,
            in PosDataFrame last,
            float dt)
        {
            if (dt <= EnhancedPredictionStartSecs || !last.IsValid())
            {
                return;
            }

            Vector3 position = last.position;
            Vector3 velocity = last.velocity;
            Vector3 angularVelocity = last.angularVelocity;
            Quaternion rotation = last.rotation;
            ResolvePredictionInputs(
                __instance,
                in last,
                dt,
                out Vector3 localAcceleration,
                out float angularAcceleration,
                out SteeringHistoryPredictor.Prediction steeringPrediction);

            int steps = Mathf.Max(1, Mathf.CeilToInt(dt / PredictionStepSecs));
            float stepDt = dt / steps;

            for (int i = 0; i < steps; i++)
            {
                Vector3 worldAcceleration = Vector3.ClampMagnitude(rotation * localAcceleration, MaxLinearAcceleration);
                position += velocity * stepDt + worldAcceleration * (0.5f * stepDt * stepDt);
                velocity += worldAcceleration * stepDt;

                Vector3 angularAccelerationVector = rotation * Vector3.up * angularAcceleration;
                Vector3 midpointAngularVelocity = angularVelocity + angularAccelerationVector * (0.5f * stepDt);
                float angularSpeed = midpointAngularVelocity.magnitude;
                if (angularSpeed > Mathf.Epsilon)
                {
                    Quaternion stepRotation = Quaternion.AngleAxis(
                        angularSpeed * Mathf.Rad2Deg * stepDt,
                        midpointAngularVelocity / angularSpeed);
                    rotation = stepRotation * rotation;
                }

                angularVelocity += angularAccelerationVector * stepDt;
            }

            if (!IsFinite(position) || !IsFinite(velocity) || !IsFinite(angularVelocity) || !IsFinite(rotation))
            {
                return;
            }

            float blend = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(EnhancedPredictionStartSecs, EnhancedPredictionFullSecs, dt));
            extrapolated.position = Vector3.Lerp(extrapolated.position, position, blend);
            extrapolated.velocity = Vector3.Lerp(extrapolated.velocity, velocity, blend);
            extrapolated.angularVelocity = Vector3.Lerp(extrapolated.angularVelocity, angularVelocity, blend);
            extrapolated.rotation = Quaternion.Slerp(extrapolated.rotation, rotation, blend);
            float steerBlend = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(EnhancedPredictionStartSecs, 0.08f, dt));
            extrapolated.steer = Mathf.Lerp(extrapolated.steer, steeringPrediction.Steer, steerBlend);
        }

        internal static void ResolvePredictionInputs(
            NetGameCircularInterpolator interpolator,
            in PosDataFrame last,
            float horizonSecs,
            out Vector3 localAcceleration,
            out float yawAcceleration,
            out SteeringHistoryPredictor.Prediction steeringPrediction)
        {
            localAcceleration = Vector3.ClampMagnitude(
                new Vector3(last.sideAccel, 0f, last.longAccel),
                MaxLinearAcceleration);
            yawAcceleration = Mathf.Clamp(last.angAccel, -MaxAngularAcceleration, MaxAngularAcceleration);
            steeringPrediction = SteeringHistoryPredictor.Predict(interpolator, in last, horizonSecs);

            if (!MotionHistoryEstimator.TryEstimate(interpolator, in last, out MotionHistoryEstimator.Estimate history))
            {
                return;
            }

            float horizonInfluence = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(EnhancedPredictionStartSecs, EnhancedPredictionFullSecs, horizonSecs));
            float transitionRetention = 1f - 0.85f * steeringPrediction.TransitionConfidence;
            float historyBlend = Mathf.Clamp01(0.65f * history.confidence * horizonInfluence * transitionRetention);

            Vector3 historyLocalAcceleration = Vector3.ClampMagnitude(
                history.localLinearAcceleration,
                MaxLinearAcceleration);
            historyLocalAcceleration.y = 0f;
            float historyYawAcceleration = Mathf.Clamp(
                Vector3.Dot(history.worldAngularAcceleration, last.rotation * Vector3.up),
                -MaxAngularAcceleration,
                MaxAngularAcceleration);

            localAcceleration = Vector3.Lerp(localAcceleration, historyLocalAcceleration, historyBlend);
            yawAcceleration = Mathf.Lerp(yawAcceleration, historyYawAcceleration, historyBlend);
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
