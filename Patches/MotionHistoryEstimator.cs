using HarmonyLib;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Patches
{
    internal static class MotionHistoryEstimator
    {
        private const int MaxIntervals = 3;
        private const long MinIntervalMs = 10L;
        private const long MaxIntervalMs = 100L;
        private const long MaxHistoryMs = 120L;
        private const float MaxLinearAcceleration = 45f;
        private const float MaxAngularAcceleration = 30f;
        private const float LinearOutlierFloor = 6f;
        private const float AngularOutlierFloor = 4f;
        private const float PositionSlackM = 2f;

        private static readonly float[] RecentWeights = { 1f, 0.6f, 0.35f };

        [System.ThreadStatic]
        private static Vector3[] _worldLinearScratch;

        [System.ThreadStatic]
        private static Vector3[] _localLinearScratch;

        [System.ThreadStatic]
        private static Vector3[] _worldAngularScratch;

        [System.ThreadStatic]
        private static float[] _intervalScratch;

        private static readonly AccessTools.FieldRef<NetGameCircularInterpolator, PosDataFrame[]> Data =
            AccessTools.FieldRefAccess<NetGameCircularInterpolator, PosDataFrame[]>("m_data");

        private static readonly AccessTools.FieldRef<NetGameCircularInterpolator, int> Current =
            AccessTools.FieldRefAccess<NetGameCircularInterpolator, int>("m_current");

        private static readonly AccessTools.FieldRef<NetGameCircularInterpolator, int> Count =
            AccessTools.FieldRefAccess<NetGameCircularInterpolator, int>("m_count");

        internal struct Estimate
        {
            public Vector3 worldLinearAcceleration;
            public Vector3 localLinearAcceleration;
            public Vector3 worldAngularAcceleration;
            public float confidence;
            public int intervalCount;
        }

        public static bool TryEstimate(
            NetGameCircularInterpolator interpolator,
            in PosDataFrame last,
            out Estimate estimate)
        {
            estimate = default;
            if (interpolator == null || !IsUsable(last))
            {
                return false;
            }

            PosDataFrame[] frames = Data(interpolator);
            int current = Current(interpolator);
            int available = Count(interpolator);
            if (frames == null || frames.Length == 0 || current < 0 || current >= frames.Length || available < 1)
            {
                return false;
            }

            PosDataFrame newest = frames[current];
            if (!IsUsable(newest) || newest.modelTime != last.modelTime || newest.posGeneration != last.posGeneration)
            {
                return false;
            }

            Vector3[] worldLinear = _worldLinearScratch ?? (_worldLinearScratch = new Vector3[MaxIntervals]);
            Vector3[] localLinear = _localLinearScratch ?? (_localLinearScratch = new Vector3[MaxIntervals]);
            Vector3[] worldAngular = _worldAngularScratch ?? (_worldAngularScratch = new Vector3[MaxIntervals]);
            float[] intervals = _intervalScratch ?? (_intervalScratch = new float[MaxIntervals]);
            int intervalCount = 0;
            PosDataFrame newer = newest;

            for (int offset = 1; offset <= available && intervalCount < MaxIntervals; offset++)
            {
                int index = current - offset;
                if (index < 0)
                {
                    index += frames.Length;
                }

                PosDataFrame older = frames[index];
                long intervalMs = newer.modelTime - older.modelTime;
                long historyMs = newest.modelTime - older.modelTime;
                if (!IsUsable(older) || older.posGeneration != newest.posGeneration || historyMs > MaxHistoryMs)
                {
                    break;
                }

                if (intervalMs == 0L)
                {
                    continue;
                }
                if (intervalMs < MinIntervalMs || intervalMs > MaxIntervalMs)
                {
                    break;
                }

                float dt = intervalMs * 0.001f;
                float maxTravel = Mathf.Max(newer.velocity.magnitude, older.velocity.magnitude) * dt
                    + 0.5f * MaxLinearAcceleration * dt * dt
                    + PositionSlackM;
                if ((newer.position - older.position).magnitude > maxTravel)
                {
                    break;
                }

                Vector3 linear = (newer.velocity - older.velocity) / dt;
                Vector3 angular = (newer.angularVelocity - older.angularVelocity) / dt;
                Vector3 local = Quaternion.Inverse(newest.rotation) * linear;
                if (!IsFinite(linear) || !IsFinite(local) || !IsFinite(angular))
                {
                    break;
                }

                worldLinear[intervalCount] = Vector3.ClampMagnitude(linear, MaxLinearAcceleration);
                localLinear[intervalCount] = Vector3.ClampMagnitude(local, MaxLinearAcceleration);
                worldAngular[intervalCount] = Vector3.ClampMagnitude(angular, MaxAngularAcceleration);
                intervals[intervalCount] = dt;
                intervalCount++;
                newer = older;
            }

            if (intervalCount == 0)
            {
                return false;
            }

            Vector3 robustWorldLinear = RobustWeightedAverage(
                worldLinear, intervalCount, MaxLinearAcceleration, LinearOutlierFloor, out float linearAgreement);
            Vector3 robustLocalLinear = RobustWeightedAverage(
                localLinear, intervalCount, MaxLinearAcceleration, LinearOutlierFloor, out float localAgreement);
            Vector3 robustWorldAngular = RobustWeightedAverage(
                worldAngular, intervalCount, MaxAngularAcceleration, AngularOutlierFloor, out float angularAgreement);

            float sampleConfidence = intervalCount / (float)MaxIntervals;
            float cadenceConfidence = CadenceConfidence(intervals, intervalCount);
            float agreement = (linearAgreement + localAgreement + angularAgreement) / 3f;

            estimate.worldLinearAcceleration = robustWorldLinear;
            estimate.localLinearAcceleration = robustLocalLinear;
            estimate.worldAngularAcceleration = robustWorldAngular;
            estimate.intervalCount = intervalCount;
            estimate.confidence = Mathf.Clamp01(sampleConfidence * cadenceConfidence * agreement);
            return IsFinite(estimate.worldLinearAcceleration) &&
                   IsFinite(estimate.localLinearAcceleration) &&
                   IsFinite(estimate.worldAngularAcceleration) &&
                   IsFinite(estimate.confidence);
        }

        private static Vector3 RobustWeightedAverage(
            Vector3[] samples,
            int count,
            float magnitudeLimit,
            float deviationFloor,
            out float agreement)
        {
            Vector3 center = ComponentMedian(samples, count);
            float deviationLimit = Mathf.Max(deviationFloor, center.magnitude * 1.5f);
            Vector3 sum = Vector3.zero;
            float weightSum = 0f;
            float disagreement = 0f;

            for (int i = 0; i < count; i++)
            {
                float weight = RecentWeights[i];
                Vector3 deviation = samples[i] - center;
                Vector3 resisted = center + Vector3.ClampMagnitude(deviation, deviationLimit);
                sum += resisted * weight;
                weightSum += weight;
                disagreement += Mathf.Clamp01(deviation.magnitude / (deviationLimit * 2f)) * weight;
            }

            agreement = weightSum > 0f ? 1f - disagreement / weightSum : 0f;
            return weightSum > 0f
                ? Vector3.ClampMagnitude(sum / weightSum, magnitudeLimit)
                : Vector3.zero;
        }

        private static Vector3 ComponentMedian(Vector3[] samples, int count)
        {
            return new Vector3(
                Median(samples[0].x, count > 1 ? samples[1].x : samples[0].x, count > 2 ? samples[2].x : samples[0].x, count),
                Median(samples[0].y, count > 1 ? samples[1].y : samples[0].y, count > 2 ? samples[2].y : samples[0].y, count),
                Median(samples[0].z, count > 1 ? samples[1].z : samples[0].z, count > 2 ? samples[2].z : samples[0].z, count));
        }

        private static float Median(float first, float second, float third, int count)
        {
            if (count == 1)
            {
                return first;
            }

            if (count == 2)
            {
                return (first + second) * 0.5f;
            }

            return first + second + third - Mathf.Min(first, Mathf.Min(second, third)) - Mathf.Max(first, Mathf.Max(second, third));
        }

        private static float CadenceConfidence(float[] intervals, int count)
        {
            if (count <= 1)
            {
                return 0.75f;
            }

            float mean = 0f;
            for (int i = 0; i < count; i++)
            {
                mean += intervals[i];
            }

            mean /= count;
            float maximumDeviation = 0f;
            for (int i = 0; i < count; i++)
            {
                maximumDeviation = Mathf.Max(maximumDeviation, Mathf.Abs(intervals[i] - mean));
            }

            return Mathf.Lerp(1f, 0.65f, Mathf.Clamp01(maximumDeviation / Mathf.Max(mean, 0.001f)));
        }

        private static bool IsUsable(PosDataFrame frame)
        {
            return frame.IsValid() && frame.modelTime > 0L &&
                   IsFinite(frame.rotation) && IsFinite(frame.velocity) && IsFinite(frame.angularVelocity);
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
