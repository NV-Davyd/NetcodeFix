using System;
using System.Collections.Generic;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Net
{
    internal enum PoseSampleKind
    {
        Interpolated,
        Extrapolated
    }

    internal readonly struct PoseSampleInfo
    {
        public readonly PoseSampleKind Kind;
        public readonly long LatestModelTime;
        public readonly long RequestAgeMs;
        public readonly long ExtrapolateMs;

        public PoseSampleInfo(PoseSampleKind kind, long latestModelTime, long requestAgeMs, long extrapolateMs)
        {
            Kind = kind;
            LatestModelTime = latestModelTime;
            RequestAgeMs = requestAgeMs;
            ExtrapolateMs = extrapolateMs;
        }
    }

    internal sealed class PoseBuffer
    {
        private const int Capacity = 96;
        private const long KeepPastMs = 1200L;
        private const long MaxExtrapolateMs = 120L;

        private readonly List<PoseSample> _samples = new List<PoseSample>(Capacity);
        private int _newestSequence = int.MinValue;

        public long LatestModelTime => _samples.Count == 0 ? 0L : _samples[_samples.Count - 1].Frame.modelTime;

        public bool Add(int sequence, ref PosDataFrame frame)
        {
            if (_samples.Count > 0)
            {
                long newestTime = _samples[_samples.Count - 1].Frame.modelTime;
                if (frame.modelTime < newestTime - KeepPastMs) return false;
            }

            if (_newestSequence != int.MinValue && (long)sequence <= (long)_newestSequence - Capacity)
            {
                return false;
            }

            if (sequence > _newestSequence) _newestSequence = sequence;

            for (int i = _samples.Count - 1; i >= 0; i--)
            {
                if (_samples[i].Sequence == sequence) return false;
                if (_samples[i].Frame.modelTime <= frame.modelTime)
                {
                    _samples.Insert(i + 1, new PoseSample(sequence, frame));
                    Trim();
                    return true;
                }
            }

            _samples.Insert(0, new PoseSample(sequence, frame));
            Trim();
            return true;
        }

        public bool TrySample(long modelTime, out PosDataFrame frame, out PoseSampleInfo info)
        {
            frame = PosDataFrame.Default;
            info = default;
            if (_samples.Count == 0) return false;

            DropOld(modelTime);
            if (_samples.Count == 0) return false;

            PoseSample latest = _samples[_samples.Count - 1];
            long requestAgeMs = Math.Max(0L, modelTime - latest.Frame.modelTime);
            if (modelTime >= latest.Frame.modelTime)
            {
                long dtMs = modelTime - latest.Frame.modelTime;
                if (dtMs > MaxExtrapolateMs) return false;

                frame = Extrapolate(latest.Frame, modelTime);
                info = new PoseSampleInfo(PoseSampleKind.Extrapolated, latest.Frame.modelTime, requestAgeMs, dtMs);
                return frame.IsValid();
            }

            for (int i = _samples.Count - 2; i >= 0; i--)
            {
                PosDataFrame left = _samples[i].Frame;
                PosDataFrame right = _samples[i + 1].Frame;
                if (left.modelTime <= modelTime && right.modelTime >= modelTime)
                {
                    frame = Interpolate(modelTime, left, right);
                    info = new PoseSampleInfo(PoseSampleKind.Interpolated, latest.Frame.modelTime, requestAgeMs, 0L);
                    return frame.IsValid();
                }
            }

            return false;
        }

        private void Trim()
        {
            while (_samples.Count > Capacity)
            {
                _samples.RemoveAt(0);
            }
        }

        private void DropOld(long modelTime)
        {
            long cutoff = modelTime - KeepPastMs;
            while (_samples.Count > 2 && _samples[1].Frame.modelTime < cutoff)
            {
                _samples.RemoveAt(0);
            }
        }

        private static PosDataFrame Interpolate(long modelTime, PosDataFrame left, PosDataFrame right)
        {
            long span = right.modelTime - left.modelTime;
            float t = span <= 0L ? 0f : Mathf.Clamp01((float)(modelTime - left.modelTime) / span);
            return Blend(modelTime, left, right, t);
        }

        private static PosDataFrame Extrapolate(PosDataFrame latest, long modelTime)
        {
            float dt = (modelTime - latest.modelTime) / 1000f;
            PosDataFrame result = latest;
            result.modelTime = modelTime;

            Vector3 accel = Vector3.zero;
            if ((Mathf.Abs(latest.sideAccel) > Mathf.Epsilon || Mathf.Abs(latest.longAccel) > Mathf.Epsilon)
                && !latest.rotation.IsOneOfNaNOrInfinity(false))
            {
                accel = latest.rotation * new Vector3(latest.sideAccel, 0f, latest.longAccel);
            }

            result.velocity = latest.velocity + accel * dt;
            result.position = latest.position + latest.velocity * dt + accel * dt * dt * 0.5f;
            result.rotation = latest.rotation * Quaternion.Euler(latest.angularVelocity * Mathf.Rad2Deg * dt);
            return result;
        }

        private static PosDataFrame Blend(long modelTime, PosDataFrame left, PosDataFrame right, float t)
        {
            PosDataFrame result = PosDataFrame.Default;
            result.modelTime = modelTime;
            result.posGeneration = left.posGeneration;
            result.flags = left.flags;
            result.accelerate = Mathf.Lerp(left.accelerate, right.accelerate, t);
            result.gear = t < 0.5f ? left.gear : right.gear;
            result.brake = Mathf.Lerp(left.brake, right.brake, t);
            result.handbrake = Mathf.Lerp(left.handbrake, right.handbrake, t);
            result.flares = t < 0.5f ? left.flares : right.flares;
            result.steer = Mathf.Lerp(left.steer, right.steer, t);
            result.clutch = Mathf.Lerp(left.clutch, right.clutch, t);
            result.autoClutch = t < 0.5f ? left.autoClutch : right.autoClutch;
            result.clutchKick = t < 0.5f ? left.clutchKick : right.clutchKick;
            result.rpm = Mathf.Lerp(left.rpm, right.rpm, t);
            result.mileage = Mathf.Lerp(left.mileage, right.mileage, t);
            result.velocity = Vector3.Lerp(left.velocity, right.velocity, t);
            result.angularVelocity = Vector3.Lerp(left.angularVelocity, right.angularVelocity, t);
            result.position = Vector3.Lerp(left.position, right.position, t);
            result.rotation = Quaternion.Lerp(left.rotation, right.rotation, t);
            result.angAccel = Mathf.Lerp(left.angAccel, right.angAccel, t);
            result.longAccel = Mathf.Lerp(left.longAccel, right.longAccel, t);
            result.sideAccel = Mathf.Lerp(left.sideAccel, right.sideAccel, t);
            result.processEngineStart = t < 0.5f ? left.processEngineStart : right.processEngineStart;
            result.ignitionEnabled = t < 0.5f ? left.ignitionEnabled : right.ignitionEnabled;
            result.starterEnabled = t < 0.5f ? left.starterEnabled : right.starterEnabled;
            result.engineStartTime = Mathf.Lerp(left.engineStartTime, right.engineStartTime, t);
            return result;
        }

        private readonly struct PoseSample
        {
            public readonly int Sequence;
            public readonly PosDataFrame Frame;

            public PoseSample(int sequence, PosDataFrame frame)
            {
                Sequence = sequence;
                Frame = frame;
            }
        }
    }
}
