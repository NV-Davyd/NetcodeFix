using HarmonyLib;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Patches
{
    internal static class SteeringHistoryPredictor
    {
        private const int MaxFrames = 4;
        private const float MinIntervalSecs = 0.01f;
        private const float MaxIntervalSecs = 0.1f;
        private const float MaxHistorySecs = 0.12f;
        private const float PredictionStartSecs = 0.04f;
        private const float FullRateUntilSecs = 0.1f;
        private const float RateDecayEndSecs = 0.15f;
        private const float MaxSteerRate = 12f;
        private const float MinSteer = -1f;
        private const float MaxSteer = 1f;
        private const float MaxLinearAcceleration = 45f;
        private const float PositionSlackM = 2f;

        private static readonly float[] IntervalWeights = { 1f, 0.6f, 0.35f };

        [System.ThreadStatic]
        private static PosDataFrame[] _historyScratch;

        [System.ThreadStatic]
        private static float[] _rateScratch;

        [System.ThreadStatic]
        private static float[] _intervalScratch;

        [System.ThreadStatic]
        private static float[] _deviationScratch;

        private static readonly AccessTools.FieldRef<NetGameCircularInterpolator, PosDataFrame[]> Data =
            AccessTools.FieldRefAccess<NetGameCircularInterpolator, PosDataFrame[]>("m_data");

        private static readonly AccessTools.FieldRef<NetGameCircularInterpolator, int> Current =
            AccessTools.FieldRefAccess<NetGameCircularInterpolator, int>("m_current");

        private static readonly AccessTools.FieldRef<NetGameCircularInterpolator, int> Count =
            AccessTools.FieldRefAccess<NetGameCircularInterpolator, int>("m_count");

        internal readonly struct Prediction
        {
            public Prediction(float steer, float transitionConfidence, float historyConfidence, int frameCount)
            {
                Steer = steer;
                TransitionConfidence = transitionConfidence;
                HistoryConfidence = historyConfidence;
                FrameCount = frameCount;
            }

            public float Steer { get; }
            public float TransitionConfidence { get; }
            public float HistoryConfidence { get; }
            public int FrameCount { get; }
        }

        /// <summary>
        /// Predicts steering from up to four recent, ordered samples. Invalid or insufficient
        /// history safely returns the last received steering value with zero confidence.
        /// </summary>
        public static Prediction Predict(
            NetGameCircularInterpolator interpolator,
            in PosDataFrame last,
            float horizonSecs)
        {
            float fallbackSteer = IsFinite(last.steer) ? Mathf.Clamp(last.steer, MinSteer, MaxSteer) : 0f;
            if (interpolator == null || !last.IsValid() || !IsFinite(last.steer) ||
                !IsFinite(horizonSecs) || horizonSecs <= 0f)
            {
                return new Prediction(fallbackSteer, 0f, 0f, 0);
            }

            PosDataFrame[] frames = Data(interpolator);
            int current = Current(interpolator);
            int available = Count(interpolator);
            if (frames == null || frames.Length == 0 || current < 0 || current >= frames.Length || available < 1 ||
                frames[current].modelTime != last.modelTime || frames[current].posGeneration != last.posGeneration)
            {
                return new Prediction(fallbackSteer, 0f, 0f, 0);
            }

            PosDataFrame[] history = _historyScratch ?? (_historyScratch = new PosDataFrame[MaxFrames]);
            history[0] = last;
            int frameCount = 1;
            PosDataFrame newer = last;

            // m_current is the newest slot. Walk backward and stop at the first gap so old
            // generations or overwritten ring entries cannot contaminate the estimate.
            for (int offset = 1; offset <= available && frameCount < MaxFrames; offset++)
            {
                int index = current - offset;
                if (index < 0)
                {
                    index += frames.Length;
                }

                PosDataFrame older = frames[index];
                if (!older.IsValid() || older.posGeneration != last.posGeneration ||
                    !IsFinite(older.steer) || !IsFinite(older.brake) || !IsFinite(older.handbrake))
                {
                    break;
                }

                float intervalSecs = (newer.modelTime - older.modelTime) * 0.001f;
                float totalHistorySecs = (last.modelTime - older.modelTime) * 0.001f;
                if (totalHistorySecs > MaxHistorySecs)
                {
                    break;
                }
                if (intervalSecs == 0f)
                {
                    continue;
                }
                if (intervalSecs < MinIntervalSecs || intervalSecs > MaxIntervalSecs)
                {
                    break;
                }

                float maxTravel = Mathf.Max(newer.velocity.magnitude, older.velocity.magnitude) * intervalSecs
                    + 0.5f * MaxLinearAcceleration * intervalSecs * intervalSecs
                    + PositionSlackM;
                if ((newer.position - older.position).magnitude > maxTravel)
                {
                    break;
                }

                history[frameCount++] = older;
                newer = older;
            }

            if (frameCount < 2)
            {
                return new Prediction(fallbackSteer, 0f, 0f, frameCount);
            }

            int rateCount = frameCount - 1;
            float[] rates = _rateScratch ?? (_rateScratch = new float[MaxFrames - 1]);
            float[] intervals = _intervalScratch ?? (_intervalScratch = new float[MaxFrames - 1]);
            for (int i = 0; i < rateCount; i++)
            {
                intervals[i] = (history[i].modelTime - history[i + 1].modelTime) * 0.001f;
                rates[i] = Mathf.Clamp(
                    (history[i].steer - history[i + 1].steer) / intervals[i],
                    -MaxSteerRate,
                    MaxSteerRate);
            }

            float medianRate = Median(rates, rateCount);
            float[] deviations = _deviationScratch ?? (_deviationScratch = new float[MaxFrames - 1]);
            for (int i = 0; i < rateCount; i++)
            {
                deviations[i] = Mathf.Abs(rates[i] - medianRate);
            }

            // A fixed floor keeps genuine fast counter-steer from being flattened when the
            // preceding samples were steady; MAD rejects a single timestamp/jitter spike.
            float deviationLimit = Mathf.Max(2f, 3f * Median(deviations, rateCount));
            float weightedRate = 0f;
            float weightSum = 0f;
            float agreementSum = 0f;
            float transitionConfidence = DetectTransition(history, rates, frameCount);
            for (int i = 0; i < rateCount; i++)
            {
                float filteredRate = Mathf.Clamp(rates[i], medianRate - deviationLimit, medianRate + deviationLimit);
                float weight = IntervalWeights[i];
                if (i == 1)
                {
                    weight *= Mathf.Lerp(1f, 0.2f, transitionConfidence);
                }
                else if (i >= 2)
                {
                    weight *= 1f - transitionConfidence;
                }
                weightedRate += filteredRate * weight;
                weightSum += weight;
                agreementSum += weight * (1f - Mathf.Clamp01(Mathf.Abs(rates[i] - medianRate) / MaxSteerRate));
            }

            weightedRate = Mathf.Clamp(weightedRate / weightSum, -MaxSteerRate, MaxSteerRate);
            float historyConfidence = FrameCountConfidence(frameCount) * Mathf.Clamp01(agreementSum / weightSum);
            float effectivePredictionSecs = EffectivePredictionTime(Mathf.Max(0f, horizonSecs));
            float predictedSteer = Mathf.Clamp(
                fallbackSteer + weightedRate * effectivePredictionSecs * historyConfidence,
                MinSteer,
                MaxSteer);

            return new Prediction(predictedSteer, transitionConfidence, historyConfidence, frameCount);
        }

        private static float DetectTransition(PosDataFrame[] history, float[] rates, int frameCount)
        {
            float latestRate = rates[0];
            float rapidCounterSteer = Mathf.InverseLerp(2.5f, 8f, Mathf.Abs(latestRate));
            float reversingCurrentSteer = history[0].steer * latestRate < 0f
                ? rapidCounterSteer * Mathf.InverseLerp(0.15f, 0.6f, Mathf.Abs(history[0].steer))
                : 0f;

            float rateReversal = 0f;
            if (frameCount >= 3 && latestRate * rates[1] < 0f)
            {
                float weakerRate = Mathf.Min(Mathf.Abs(latestRate), Mathf.Abs(rates[1]));
                rateReversal = Mathf.InverseLerp(1.5f, 6f, weakerRate);
            }

            float handbrakeChange = Mathf.InverseLerp(
                0.1f,
                0.7f,
                Mathf.Abs(history[0].handbrake - history[1].handbrake));
            float brakeChange = Mathf.InverseLerp(
                0.15f,
                0.75f,
                Mathf.Abs(history[0].brake - history[1].brake));

            return Mathf.Clamp01(Mathf.Max(
                Mathf.Max(reversingCurrentSteer, rateReversal),
                Mathf.Max(handbrakeChange, brakeChange)));
        }

        private static float EffectivePredictionTime(float horizonSecs)
        {
            if (horizonSecs <= PredictionStartSecs)
            {
                return 0f;
            }

            float fullRateDuration = Mathf.Min(horizonSecs, FullRateUntilSecs) - PredictionStartSecs;
            if (horizonSecs <= FullRateUntilSecs)
            {
                return fullRateDuration;
            }

            float decayDuration = RateDecayEndSecs - FullRateUntilSecs;
            float elapsedDecay = Mathf.Min(horizonSecs - FullRateUntilSecs, decayDuration);
            float decayArea = elapsedDecay - elapsedDecay * elapsedDecay / (2f * decayDuration);
            return fullRateDuration + decayArea;
        }

        private static float FrameCountConfidence(int frameCount)
        {
            switch (frameCount)
            {
                case 2: return 0.35f;
                case 3: return 0.7f;
                default: return 1f;
            }
        }

        private static float Median(float[] values, int count)
        {
            if (count == 1)
            {
                return values[0];
            }

            if (count == 2)
            {
                return 0.5f * (values[0] + values[1]);
            }

            return values[0] + values[1] + values[2] -
                   Mathf.Min(values[0], Mathf.Min(values[1], values[2])) -
                   Mathf.Max(values[0], Mathf.Max(values[1], values[2]));
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
