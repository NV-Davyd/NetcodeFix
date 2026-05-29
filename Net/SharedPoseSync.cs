using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CarX;
using HarmonyLib;
using KSL.API;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Net
{
    internal static class SharedPoseSync
    {
        private const string PacketName = "netcodefix_pose_v10";
        private const int Schema = 10;
        private const float RegularSendIntervalSecs = 0.02f;
        private const float BurstSendIntervalSecs = 0.01f;
        private const float BurstHoldSecs = 0.14f;
        private const float MaxAutoMapDistanceM = 40f;

        private static readonly FieldInfo F_syncTime =
            AccessTools.Field(typeof(NetGameSyncPosSystem), "m_syncTime");

        private static readonly Dictionary<int, PoseBuffer> Buffers = new Dictionary<int, PoseBuffer>();
        private static readonly Dictionary<int, long> ReceiveTimelineOffsets = new Dictionary<int, long>();
        private static readonly Dictionary<int, int> ClaimedToMappedNetworkIds = new Dictionary<int, int>();
        private static readonly Dictionary<int, PeerDebugState> PeerDebug = new Dictionary<int, PeerDebugState>();
        private static bool _ready;
        private static int _packetId = -1;
        private static float _lastSend = -999f;
        private static float _burstUntil;
        private static bool _hasObservedFrame;
        private static PosDataFrame _lastObservedFrame;
        private static int _sequence;
        private static float _rateWindowStart;
        private static float _nextRateUpdate;
        private static long _windowSent;
        private static long _windowBurstSent;
        private static long _windowReceived;
        private static long _windowAccepted;
        private static long _windowDropped;
        private static long _windowUsed;
        private static long _windowFresh;
        private static long _windowStale;
        private static long _windowNoBuffer;
        private static long _windowNoSample;
        private static long _windowAgeSamples;
        private static long _windowNativeAgeSum;
        private static long _windowSharedAgeSum;
        private static long _windowAdvantageSum;
        private static long _windowUsedAgeSamples;
        private static long _windowUsedNativeAgeSum;
        private static long _windowUsedSharedAgeSum;
        private static long _windowUsedAdvantageSum;

        public static long FramesSent;
        public static long BurstFramesSent;
        public static long FramesReceived;
        public static long FramesAccepted;
        public static long FramesDropped;
        public static long FramesUsed;
        public static long SamplesInterpolated;
        public static long SamplesExtrapolated;
        public static long FallbackNoBuffer;
        public static long FallbackStale;
        public static long FallbackNoSample;
        public static long LastNativeAgeMs;
        public static long LastSharedAgeMs;
        public static long LastSharedExtrapolateMs;
        public static long LastTimelineOffsetMs;
        public static string LastPeerMap = "-";
        public static string RateLine = "Rate/s: -";
        public static string QualityLine = "Quality: -";

        public static int ModdedPeerCount => Buffers.Count;

        public static void Initialize()
        {
            Buffers.Clear();
            ReceiveTimelineOffsets.Clear();
            ClaimedToMappedNetworkIds.Clear();
            PeerDebug.Clear();
            FramesSent = 0;
            BurstFramesSent = 0;
            FramesReceived = 0;
            FramesAccepted = 0;
            FramesDropped = 0;
            FramesUsed = 0;
            SamplesInterpolated = 0;
            SamplesExtrapolated = 0;
            FallbackNoBuffer = 0;
            FallbackStale = 0;
            FallbackNoSample = 0;
            LastNativeAgeMs = 0;
            LastSharedAgeMs = 0;
            LastSharedExtrapolateMs = 0;
            LastTimelineOffsetMs = 0;
            LastPeerMap = "-";
            RateLine = "Rate/s: -";
            QualityLine = "Quality: -";
            _lastSend = -999f;
            _burstUntil = 0f;
            _hasObservedFrame = false;
            _lastObservedFrame = PosDataFrame.Default;
            ResetRateWindow(Time.unscaledTime);

            if (!Kino.Sync.IsAvailable)
            {
                Kino.Log.Warning("[NetcodeFix] Kino.Sync unavailable; shared pose packets disabled.");
                return;
            }

            if (!Kino.Sync.TryRegisterPacket(PacketName, out _packetId))
            {
                Kino.Log.Warning("[NetcodeFix] Unable to register shared pose packet.");
                return;
            }

            if (!Kino.Sync.SetCallback(_packetId, OnReceive))
            {
                Kino.Log.Warning("[NetcodeFix] Unable to register shared pose callback.");
                return;
            }

            _ready = true;
            Kino.Log.Info("[NetcodeFix] Shared pose sync initialized.");
        }

        public static void Shutdown()
        {
            _ready = false;
            Buffers.Clear();
        }

        public static void Tick()
        {
            if (_ready) UpdateRateSnapshot();
        }

        public static void FixedTick()
        {
            if (!_ready || !Settings.Enabled || !Settings.SharePosePackets) return;

            if (!TryBuildLocalFrame(out PosDataFrame frame)) return;

            float now = Time.unscaledTime;
            if (IsTransitionFrame(ref frame, ref _lastObservedFrame))
            {
                _burstUntil = Mathf.Max(_burstUntil, now + BurstHoldSecs);
            }

            bool burstActive = now < _burstUntil;
            float interval = burstActive ? BurstSendIntervalSecs : RegularSendIntervalSecs;

            if (now - _lastSend >= interval)
            {
                BroadcastLocalPose(ref frame, burstActive);
                _lastSend = now;
            }

            _lastObservedFrame = frame;
            _hasObservedFrame = true;
        }

        public static bool TrySample(int networkId, long modelTime, long nativeLatestTime, out PosDataFrame frame)
        {
            frame = PosDataFrame.Default;
            if (!_ready || !Settings.Enabled || !Settings.SharePosePackets) return false;

            LastNativeAgeMs = Math.Max(0L, modelTime - nativeLatestTime);
            PeerDebugState debug = GetPeerDebug(networkId);
            debug.MappedNetworkId = networkId;
            debug.MappedName = ResolvePlayerName(networkId, debug.MappedName);
            debug.LastSampleModelTime = modelTime;
            debug.LastNativeLatestTime = nativeLatestTime;
            debug.LastNativeAgeMs = LastNativeAgeMs;

            if (!Buffers.TryGetValue(networkId, out PoseBuffer buffer))
            {
                FallbackNoBuffer++;
                debug.NoBufferSamples++;
                RecordNoBuffer(debug);
                debug.LastReason = "no buffer";
                return false;
            }

            if (ReceiveTimelineOffsets.TryGetValue(networkId, out long offset))
            {
                LastTimelineOffsetMs = offset;
                debug.ClockOffsetMs = offset;
            }

            LastSharedAgeMs = Math.Max(0L, modelTime - buffer.LatestModelTime);
            debug.LastSharedLatestTime = buffer.LatestModelTime;
            debug.LastSharedAgeMs = LastSharedAgeMs;
            debug.LastStaleDeltaMs = nativeLatestTime - buffer.LatestModelTime;
            RecordAgeSample(debug, LastNativeAgeMs, LastSharedAgeMs);

            if (buffer.LatestModelTime + 5L < nativeLatestTime)
            {
                FallbackStale++;
                debug.StaleSamples++;
                RecordStale(debug);
                debug.LastReason = "stale";
                return false;
            }

            if (!buffer.TrySample(modelTime, out frame, out PoseSampleInfo info))
            {
                FallbackNoSample++;
                debug.NoSampleSamples++;
                RecordNoSample(debug);
                debug.LastReason = "no sample";
                return false;
            }

            FramesUsed++;
            debug.UsedSamples++;
            RecordUsed(debug, info.Kind);
            LastSharedAgeMs = Math.Max(0L, modelTime - info.LatestModelTime);
            RecordUsedAgeSample(debug, LastNativeAgeMs, LastSharedAgeMs);
            LastSharedExtrapolateMs = info.ExtrapolateMs;
            frame.modelTime = modelTime;
            debug.LastSharedAgeMs = LastSharedAgeMs;
            debug.LastPredictMs = info.ExtrapolateMs;
            debug.LastReason = info.Kind == PoseSampleKind.Extrapolated ? "used predict" : "used interp";

            if (info.Kind == PoseSampleKind.Extrapolated)
            {
                SamplesExtrapolated++;
                debug.PredictSamples++;
            }
            else
            {
                SamplesInterpolated++;
                debug.InterpSamples++;
            }

            return true;
        }

        private static bool TryBuildLocalFrame(out PosDataFrame frame)
        {
            frame = PosDataFrame.Default;
            try
            {
                NetworkGame game = NetworkController.InstanceGame;
                NetworkPlayer localPlayer = game?.LocalPlayer;
                RaceCar raceCar = localPlayer?.userCar;
                if (localPlayer == null || raceCar?.carX == null || raceCar.getRigidbody == null) return false;

                if (!TryGetModelFixedTimeMs(out long modelTime)) return false;

                BuildLocalFrame(ref frame, localPlayer, raceCar, modelTime);
                return frame.IsValid();
            }
            catch (Exception ex)
            {
                Kino.Log.Warning("[NetcodeFix] Shared pose capture failed: " + ex.Message);
                return false;
            }
        }

        private static void BroadcastLocalPose(ref PosDataFrame frame, bool burst)
        {
            try
            {
                NetworkPlayer localPlayer = NetworkController.InstanceGame?.LocalPlayer;
                if (localPlayer == null) return;

                IPacket packet = Kino.Sync.CreatePacket(_packetId);
                WriteFrame(packet, localPlayer.NetworkID, GetPlayerDisplayName(localPlayer), ++_sequence, ref frame);

                if (Kino.Sync.SendToAll(packet))
                {
                    FramesSent++;
                    _windowSent++;
                    if (burst) BurstFramesSent++;
                    if (burst) _windowBurstSent++;
                }
            }
            catch (Exception ex)
            {
                Kino.Log.Warning("[NetcodeFix] Shared pose send failed: " + ex.Message);
            }
        }

        private static bool IsTransitionFrame(ref PosDataFrame current, ref PosDataFrame previous)
        {
            if (!_hasObservedFrame || !previous.IsValid()) return false;

            if (current.gear != previous.gear) return true;
            if (Mathf.Abs(current.steer - previous.steer) > 0.12f) return true;
            if (Mathf.Abs(current.brake - previous.brake) > 0.3f) return true;
            if (Mathf.Abs(current.handbrake - previous.handbrake) > 0.3f) return true;
            if (Mathf.Abs(current.clutch - previous.clutch) > 0.35f) return true;
            if (Mathf.Abs(current.accelerate - previous.accelerate) > 0.35f) return true;
            if (Mathf.Abs(current.angularVelocity.y - previous.angularVelocity.y) > 0.65f) return true;

            return Vector3.SqrMagnitude(current.velocity - previous.velocity) > 16f;
        }

        private static void OnReceive(IPacket packet)
        {
            if (packet.Id != _packetId) return;
            FramesReceived++;
            _windowReceived++;

            try
            {
                if (!ReadFrame(packet, out int senderNetworkId, out string senderName, out int sequence, out PosDataFrame frame))
                {
                    FramesDropped++;
                    _windowDropped++;
                    return;
                }

                int localNetworkId = NetworkController.InstanceGame?.LocalPlayer?.NetworkID ?? -1;
                if (senderNetworkId == localNetworkId || senderNetworkId < 0 || !frame.IsValid())
                {
                    FramesDropped++;
                    _windowDropped++;
                    return;
                }

                if (!TryResolveMappedNetworkId(senderNetworkId, ref frame, out int mappedNetworkId, out long nativeLatestTime, out float matchDistance))
                {
                    PeerDebugState senderDebug = GetPeerDebug(senderNetworkId);
                    senderDebug.ClaimedName = CleanPlayerName(senderName);
                    senderDebug.MappedName = ResolvePlayerName(senderNetworkId, senderName);
                    senderDebug.DroppedPackets++;
                    senderDebug.WindowDropped++;
                    senderDebug.LastReason = "map fail";
                    FramesDropped++;
                    _windowDropped++;
                    return;
                }

                if (!AlignFrameToLocalTimeline(mappedNetworkId, nativeLatestTime, ref frame))
                {
                    PeerDebugState mappedDebug = GetPeerDebug(mappedNetworkId);
                    mappedDebug.DroppedPackets++;
                    mappedDebug.WindowDropped++;
                    mappedDebug.LastReason = "align fail";
                    FramesDropped++;
                    _windowDropped++;
                    return;
                }

                string claimedName = CleanPlayerName(senderName);
                string mappedName = ResolvePlayerName(mappedNetworkId, senderName);
                LastPeerMap = FormatNameForMap(claimedName, senderNetworkId)
                    + " -> " + FormatNameForMap(mappedName, mappedNetworkId)
                    + " (" + matchDistance.ToString("F1") + "m)";
                PeerDebugState debug = GetPeerDebug(mappedNetworkId);
                debug.ClaimedNetworkId = senderNetworkId;
                debug.MappedNetworkId = mappedNetworkId;
                debug.ClaimedName = claimedName;
                debug.MappedName = mappedName;
                debug.ReceivedPackets++;
                debug.WindowReceived++;
                debug.LastRawModelTime = frame.modelTime - (ReceiveTimelineOffsets.TryGetValue(mappedNetworkId, out long receiveOffset) ? receiveOffset : 0L);
                debug.LastAlignedModelTime = frame.modelTime;
                debug.LastNativeAtReceiveTime = nativeLatestTime;
                debug.ClockOffsetMs = receiveOffset;
                debug.MatchDistanceM = matchDistance;
                debug.LastReason = "recv";

                if (!Buffers.TryGetValue(mappedNetworkId, out PoseBuffer buffer))
                {
                    buffer = new PoseBuffer();
                    Buffers[mappedNetworkId] = buffer;
                }

                if (buffer.Add(sequence, ref frame))
                {
                    FramesAccepted++;
                    _windowAccepted++;
                    debug.AcceptedPackets++;
                    debug.WindowAccepted++;
                    debug.LastSharedLatestTime = buffer.LatestModelTime;
                    debug.LastReason = "accepted";
                }
                else
                {
                    FramesDropped++;
                    _windowDropped++;
                    debug.DroppedPackets++;
                    debug.WindowDropped++;
                    debug.LastReason = "buffer reject";
                }
            }
            catch (Exception ex)
            {
                FramesDropped++;
                _windowDropped++;
                Kino.Log.Warning("[NetcodeFix] Shared pose receive failed: " + ex.Message);
            }
        }

        private static bool AlignFrameToLocalTimeline(int mappedNetworkId, long nativeLatestTime, ref PosDataFrame frame)
        {
            long rawModelTime = frame.modelTime;
            long candidateOffset = nativeLatestTime - rawModelTime;
            long offset;

            if (!ReceiveTimelineOffsets.TryGetValue(mappedNetworkId, out offset))
            {
                offset = candidateOffset;
                ReceiveTimelineOffsets[mappedNetworkId] = offset;
            }
            else if (Math.Abs(candidateOffset - offset) > 1000L)
            {
                offset = candidateOffset;
                ReceiveTimelineOffsets[mappedNetworkId] = offset;
            }
            else
            {
                offset += (candidateOffset - offset) / 8L;
                ReceiveTimelineOffsets[mappedNetworkId] = offset;
            }

            long alignedModelTime = rawModelTime + offset;

            LastTimelineOffsetMs = offset;
            frame.modelTime = alignedModelTime;
            return frame.modelTime > 0L;
        }

        public static IEnumerable<string> GetDebugLines(int maxLines)
        {
            int count = 0;
            foreach (PeerDebugState debug in GetSortedDebugStates())
            {
                if (count++ >= maxLines) yield break;
                yield return debug.Format();
            }
        }

        public static IEnumerable<string> GetQualityDebugLines(int maxLines)
        {
            int count = 0;
            foreach (PeerDebugState debug in GetSortedDebugStates())
            {
                if (count++ >= maxLines) yield break;
                yield return debug.FormatQuality();
            }
        }

        public static IEnumerable<string> GetActiveDebugLines(int maxLines)
        {
            int count = 0;
            foreach (PeerDebugState debug in GetSortedDebugStates())
            {
                if (!debug.HasSharedTraffic) continue;
                if (count++ >= maxLines) yield break;
                yield return debug.Format();
            }
        }

        public static IEnumerable<string> GetActiveQualityDebugLines(int maxLines)
        {
            int count = 0;
            foreach (PeerDebugState debug in GetSortedDebugStates())
            {
                if (!debug.HasSharedTraffic) continue;
                if (count++ >= maxLines) yield break;
                yield return debug.FormatQuality();
            }
        }

        private static List<PeerDebugState> GetSortedDebugStates()
        {
            List<PeerDebugState> debugStates = new List<PeerDebugState>(PeerDebug.Values);
            debugStates.Sort((left, right) => right.SortScore.CompareTo(left.SortScore));
            return debugStates;
        }

        private static PeerDebugState GetPeerDebug(int networkId)
        {
            if (!PeerDebug.TryGetValue(networkId, out PeerDebugState debug))
            {
                debug = new PeerDebugState(networkId);
                PeerDebug[networkId] = debug;
            }

            return debug;
        }

        private static bool TryResolveMappedNetworkId(
            int claimedNetworkId,
            ref PosDataFrame frame,
            out int mappedNetworkId,
            out long nativeLatestTime,
            out float matchDistance)
        {
            mappedNetworkId = -1;
            nativeLatestTime = 0L;
            matchDistance = 0f;

            if (ClaimedToMappedNetworkIds.TryGetValue(claimedNetworkId, out int cachedMappedId)
                && TryGetNativeLatestFrame(cachedMappedId, out PosDataFrame cachedNative)
                && cachedNative.IsValid())
            {
                matchDistance = Vector3.Distance(cachedNative.position, frame.position);
                if (matchDistance <= MaxAutoMapDistanceM)
                {
                    mappedNetworkId = cachedMappedId;
                    nativeLatestTime = cachedNative.modelTime;
                    return true;
                }

                ClaimedToMappedNetworkIds.Remove(claimedNetworkId);
            }

            NetworkGame game = NetworkController.InstanceGame;
            if (game?.Players == null) return false;

            int localNetworkId = game.LocalPlayer?.NetworkID ?? -1;
            int remoteCount = 0;
            int bestNetworkId = -1;
            long bestNativeTime = 0L;
            float bestSqr = float.MaxValue;

            foreach (NetworkPlayer player in game.Players)
            {
                if (player == null || player.NetworkID < 0 || player.NetworkID == localNetworkId || player.IsLocal)
                {
                    continue;
                }

                if (!TryGetNativeLatestFrame(player.NetworkID, out PosDataFrame nativeFrame) || !nativeFrame.IsValid())
                {
                    continue;
                }

                remoteCount++;
                float sqr = (nativeFrame.position - frame.position).sqrMagnitude;

                if (player.NetworkID == claimedNetworkId && sqr <= MaxAutoMapDistanceM * MaxAutoMapDistanceM)
                {
                    bestNetworkId = player.NetworkID;
                    bestNativeTime = nativeFrame.modelTime;
                    bestSqr = sqr;
                    break;
                }

                if (sqr < bestSqr)
                {
                    bestNetworkId = player.NetworkID;
                    bestNativeTime = nativeFrame.modelTime;
                    bestSqr = sqr;
                }
            }

            if (bestNetworkId < 0) return false;

            matchDistance = Mathf.Sqrt(bestSqr);
            if (remoteCount > 1 && matchDistance > MaxAutoMapDistanceM)
            {
                return false;
            }

            ClaimedToMappedNetworkIds[claimedNetworkId] = bestNetworkId;
            mappedNetworkId = bestNetworkId;
            nativeLatestTime = bestNativeTime;
            return nativeLatestTime > 0L;
        }

        private static bool TryGetNativeLatestTime(int networkId, out long nativeLatestTime)
        {
            nativeLatestTime = 0L;
            if (!TryGetNativeLatestFrame(networkId, out PosDataFrame frame)) return false;
            nativeLatestTime = frame.modelTime;
            return nativeLatestTime > 0L;
        }

        private static void RecordNoBuffer(PeerDebugState debug)
        {
            _windowNoBuffer++;
            debug.WindowNoBuffer++;
        }

        private static void RecordAgeSample(PeerDebugState debug, long nativeAge, long sharedAge)
        {
            long advantage = nativeAge - sharedAge;

            _windowAgeSamples++;
            _windowNativeAgeSum += nativeAge;
            _windowSharedAgeSum += sharedAge;
            _windowAdvantageSum += advantage;
            if (advantage >= -5L) _windowFresh++;

            debug.WindowAgeSamples++;
            debug.WindowNativeAgeSum += nativeAge;
            debug.WindowSharedAgeSum += sharedAge;
            debug.WindowAdvantageSum += advantage;
            if (advantage >= -5L) debug.WindowFresh++;
        }

        private static void RecordStale(PeerDebugState debug)
        {
            _windowStale++;
            debug.WindowStale++;
        }

        private static void RecordNoSample(PeerDebugState debug)
        {
            _windowNoSample++;
            debug.WindowNoSample++;
        }

        private static void RecordUsed(PeerDebugState debug, PoseSampleKind kind)
        {
            _windowUsed++;
            debug.WindowUsed++;

            if (kind == PoseSampleKind.Extrapolated)
            {
                debug.WindowPredict++;
            }
            else
            {
                debug.WindowInterp++;
            }
        }

        private static void RecordUsedAgeSample(PeerDebugState debug, long nativeAge, long sharedAge)
        {
            long advantage = nativeAge - sharedAge;

            _windowUsedAgeSamples++;
            _windowUsedNativeAgeSum += nativeAge;
            _windowUsedSharedAgeSum += sharedAge;
            _windowUsedAdvantageSum += advantage;

            debug.WindowUsedAgeSamples++;
            debug.WindowUsedNativeAgeSum += nativeAge;
            debug.WindowUsedSharedAgeSum += sharedAge;
            debug.WindowUsedAdvantageSum += advantage;
        }

        private static void UpdateRateSnapshot()
        {
            float now = Time.unscaledTime;
            if (_rateWindowStart <= 0f)
            {
                ResetRateWindow(now);
                return;
            }

            if (now < _nextRateUpdate) return;

            float elapsed = Mathf.Max(0.1f, now - _rateWindowStart);
            float sent = _windowSent / elapsed;
            float burst = _windowBurstSent / elapsed;
            float recv = _windowReceived / elapsed;
            float accepted = _windowAccepted / elapsed;
            float used = _windowUsed / elapsed;
            float dropped = _windowDropped / elapsed;
            float noBuffer = _windowNoBuffer / elapsed;
            float stale = _windowStale / elapsed;
            float noSample = _windowNoSample / elapsed;

            long actionable = _windowUsed + _windowStale + _windowNoSample;
            float usedPct = actionable > 0L ? 100f * _windowUsed / actionable : 0f;
            float freshPct = _windowAgeSamples > 0L ? 100f * _windowFresh / _windowAgeSamples : 0f;
            float stalePct = _windowAgeSamples > 0L ? 100f * _windowStale / _windowAgeSamples : 0f;
            float avgNative = _windowAgeSamples > 0L ? (float)_windowNativeAgeSum / _windowAgeSamples : 0f;
            float avgShared = _windowAgeSamples > 0L ? (float)_windowSharedAgeSum / _windowAgeSamples : 0f;
            float avgAdvantage = _windowAgeSamples > 0L ? (float)_windowAdvantageSum / _windowAgeSamples : 0f;
            float avgUsedNative = _windowUsedAgeSamples > 0L ? (float)_windowUsedNativeAgeSum / _windowUsedAgeSamples : 0f;
            float avgUsedShared = _windowUsedAgeSamples > 0L ? (float)_windowUsedSharedAgeSum / _windowUsedAgeSamples : 0f;
            float avgUsedAdvantage = _windowUsedAgeSamples > 0L ? (float)_windowUsedAdvantageSum / _windowUsedAgeSamples : 0f;

            RateLine = "Rate/s sent " + sent.ToString("F1")
                + " burst " + burst.ToString("F1")
                + " recv " + recv.ToString("F1")
                + " acc " + accepted.ToString("F1")
                + " used " + used.ToString("F1")
                + " drop " + dropped.ToString("F1");

            QualityLine = "Quality use " + usedPct.ToString("F0") + "%"
                + " fresh " + freshPct.ToString("F0") + "%"
                + " stale " + stalePct.ToString("F0") + "%"
                + " noBuf/s " + noBuffer.ToString("F0")
                + " noSamp/s " + noSample.ToString("F1")
                + " avg n/s " + avgNative.ToString("F0") + "/" + avgShared.ToString("F0")
                + " adv " + avgAdvantage.ToString("F0") + "ms"
                + " used " + avgUsedNative.ToString("F0") + "/" + avgUsedShared.ToString("F0")
                + " uAdv " + avgUsedAdvantage.ToString("F0") + "ms";

            foreach (PeerDebugState debug in PeerDebug.Values)
            {
                debug.UpdateRateSnapshot(elapsed);
            }

            ResetRateWindow(now);
        }

        private static void ResetRateWindow(float now)
        {
            _rateWindowStart = now;
            _nextRateUpdate = now + 1f;
            _windowSent = 0;
            _windowBurstSent = 0;
            _windowReceived = 0;
            _windowAccepted = 0;
            _windowDropped = 0;
            _windowUsed = 0;
            _windowFresh = 0;
            _windowStale = 0;
            _windowNoBuffer = 0;
            _windowNoSample = 0;
            _windowAgeSamples = 0;
            _windowNativeAgeSum = 0;
            _windowSharedAgeSum = 0;
            _windowAdvantageSum = 0;
            _windowUsedAgeSamples = 0;
            _windowUsedNativeAgeSum = 0;
            _windowUsedSharedAgeSum = 0;
            _windowUsedAdvantageSum = 0;
        }

        private static bool TryGetNativeLatestFrame(int networkId, out PosDataFrame frame)
        {
            frame = PosDataFrame.Default;
            try
            {
                var systems = NetworkController.InstanceGame?.systems;
                var syncPos = systems?.Get<NetGameSyncPosSystem>();
                if (syncPos == null) return false;

                if (!syncPos.TryGetRAWInfo(networkId, out NetGameSyncPosSystem.PosSyncInfo info)
                    || info?.interpolator == null)
                {
                    return false;
                }

                frame = info.interpolator.GetLast();
                return frame.IsValid();
            }
            catch
            {
                return false;
            }
        }

        private static void BuildLocalFrame(ref PosDataFrame frame, NetworkPlayer player, RaceCar car, long modelTime)
        {
            Car carX = car.carX;
            Rigidbody rb = car.getRigidbody;

            frame.modelTime = modelTime;
            frame.posGeneration = player.spawnGeneration;
            frame.flags = DataFrameFlags.None;

            frame.accelerate = carX.accelerate;
            frame.brake = carX.brake;
            frame.handbrake = carX.handbrake;
            frame.clutch = carX.clutch;
            frame.steer = carX.GetSteer();
            frame.gear = (byte)carX.gear;
            frame.rpm = carX.rpm;
            frame.flares = car.flares != null && car.flares.flares != null && car.flares.flares.activeSelf;

            frame.processEngineStart = true;
            frame.ignitionEnabled = car.engineController != null && car.engineController.isIgnitionOn;
            frame.starterEnabled = car.engineController != null && car.engineController.isStarterOn;
            frame.engineStartTime = car.engineController != null ? car.engineController.engineStartTime : 0f;
            frame.autoClutch = carX.autoClutch;
            frame.clutchKick = car.isClutchKick;

            frame.position = rb.position;
            frame.rotation = rb.rotation;
            frame.velocity = rb.velocity;
            frame.angularVelocity = rb.angularVelocity;
            frame.longAccel = carX.longAccel;
            frame.sideAccel = carX.sideAccel;
            frame.angAccel = carX.angAccel;
            frame.mileage = car.mileageMoreZero;
        }

        private static bool TryGetModelFixedTimeMs(out long modelTime)
        {
            modelTime = 0L;
            try
            {
                NetGameSyncTimeSystem syncTime = F_syncTime?.GetValue(null) as NetGameSyncTimeSystem;
                var systems = NetworkController.InstanceGame?.systems;
                if (syncTime == null && systems != null) syncTime = systems.Get<NetGameSyncTimeSystem>();

                if (syncTime == null || !syncTime.initialSyncDone) return false;

                modelTime = syncTime.GetModelFixedTimeMs();
                return modelTime > 0L;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteFrame(IPacket packet, int senderNetworkId, string senderName, int sequence, ref PosDataFrame frame)
        {
            packet.WriteInt32(Schema);
            packet.WriteInt32(senderNetworkId);
            packet.WriteString(CleanPlayerName(senderName));
            packet.WriteInt32(sequence);
            packet.WriteInt64(frame.modelTime);
            packet.WriteByte(frame.posGeneration);
            packet.WriteUInt32((uint)frame.flags);

            packet.WriteFloat(frame.accelerate);
            packet.WriteFloat(frame.brake);
            packet.WriteFloat(frame.handbrake);
            packet.WriteFloat(frame.clutch);
            packet.WriteFloat(frame.steer);
            packet.WriteByte(frame.gear);
            packet.WriteFloat(frame.rpm);
            packet.WriteBool(frame.flares);

            packet.WriteBool(frame.processEngineStart);
            packet.WriteBool(frame.ignitionEnabled);
            packet.WriteBool(frame.starterEnabled);
            packet.WriteFloat(frame.engineStartTime);
            packet.WriteBool(frame.autoClutch);
            packet.WriteBool(frame.clutchKick);

            WriteVector(packet, frame.position);
            WriteQuaternion(packet, frame.rotation);
            WriteVector(packet, frame.velocity);
            WriteVector(packet, frame.angularVelocity);

            packet.WriteFloat(frame.longAccel);
            packet.WriteFloat(frame.sideAccel);
            packet.WriteFloat(frame.angAccel);
            packet.WriteFloat(frame.mileage);
        }

        private static bool ReadFrame(IPacket packet, out int senderNetworkId, out string senderName, out int sequence, out PosDataFrame frame)
        {
            senderNetworkId = -1;
            senderName = string.Empty;
            sequence = 0;
            frame = PosDataFrame.Default;

            int schema = packet.ReadInt32();
            if (schema != Schema) return false;

            senderNetworkId = packet.ReadInt32();
            senderName = CleanPlayerName(packet.ReadString());
            sequence = packet.ReadInt32();
            frame.modelTime = packet.ReadInt64();
            frame.posGeneration = packet.ReadByte();
            frame.flags = (DataFrameFlags)packet.ReadUInt32();

            frame.accelerate = packet.ReadFloat();
            frame.brake = packet.ReadFloat();
            frame.handbrake = packet.ReadFloat();
            frame.clutch = packet.ReadFloat();
            frame.steer = packet.ReadFloat();
            frame.gear = packet.ReadByte();
            frame.rpm = packet.ReadFloat();
            frame.flares = packet.ReadBool();

            frame.processEngineStart = packet.ReadBool();
            frame.ignitionEnabled = packet.ReadBool();
            frame.starterEnabled = packet.ReadBool();
            frame.engineStartTime = packet.ReadFloat();
            frame.autoClutch = packet.ReadBool();
            frame.clutchKick = packet.ReadBool();

            frame.position = ReadVector(packet);
            frame.rotation = ReadQuaternion(packet);
            frame.velocity = ReadVector(packet);
            frame.angularVelocity = ReadVector(packet);

            frame.longAccel = packet.ReadFloat();
            frame.sideAccel = packet.ReadFloat();
            frame.angAccel = packet.ReadFloat();
            frame.mileage = packet.ReadFloat();
            return true;
        }

        private static string ResolvePlayerName(int networkId, string fallback)
        {
            try
            {
                NetworkPlayer player = NetworkController.InstanceGame?.FindPlayerByNetworkId(networkId);
                string name = GetPlayerDisplayName(player);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch
            {
            }

            fallback = CleanPlayerName(fallback);
            return !string.IsNullOrEmpty(fallback) ? fallback : string.Empty;
        }

        private static string GetPlayerDisplayName(NetworkPlayer player)
        {
            if (player == null) return string.Empty;

            string name = player.PlayerProperties?.Name;
            if (string.IsNullOrEmpty(name)) name = player.OriginaNickName;
            if (string.IsNullOrEmpty(name)) name = player.FilteredNickName;
            return CleanPlayerName(name);
        }

        private static string CleanPlayerName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            name = StripPlayerNameMarkup(name);
            name = name.Replace("\r", " ").Replace("\n", " ").Trim();
            if (name.Length > 24) name = name.Substring(0, 24);
            return name;
        }

        private static string StripPlayerNameMarkup(string name)
        {
            StringBuilder builder = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '<')
                {
                    int close = name.IndexOf('>', i + 1);
                    if (close >= 0)
                    {
                        i = close;
                        continue;
                    }
                }

                if (c == '[')
                {
                    int close = name.IndexOf(']', i + 1);
                    if (close >= 0 && IsRemovableNguiTag(name, i + 1, close - i - 1))
                    {
                        i = close;
                        continue;
                    }
                }

                if (c != '\u001b') builder.Append(c);
            }

            return builder.ToString();
        }

        private static bool IsRemovableNguiTag(string text, int start, int length)
        {
            if (length == 1)
            {
                char c = text[start];
                return c == '+' || c == '-' || c == 'T';
            }

            if (length == 2 && text[start] == '/' && text[start + 1] == 'T') return true;
            if (length != 6 && length != 8) return false;

            for (int i = 0; i < length; i++)
            {
                char c = text[start + i];
                bool hex = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }

            return true;
        }

        private static string FormatNameForMap(string name, int networkId)
        {
            return !string.IsNullOrEmpty(name) ? name : "id " + networkId;
        }

        private static void WriteVector(IPacket packet, Vector3 value)
        {
            packet.WriteFloat(value.x);
            packet.WriteFloat(value.y);
            packet.WriteFloat(value.z);
        }

        private static Vector3 ReadVector(IPacket packet)
        {
            return new Vector3(packet.ReadFloat(), packet.ReadFloat(), packet.ReadFloat());
        }

        private static void WriteQuaternion(IPacket packet, Quaternion value)
        {
            packet.WriteFloat(value.x);
            packet.WriteFloat(value.y);
            packet.WriteFloat(value.z);
            packet.WriteFloat(value.w);
        }

        private static Quaternion ReadQuaternion(IPacket packet)
        {
            return new Quaternion(packet.ReadFloat(), packet.ReadFloat(), packet.ReadFloat(), packet.ReadFloat());
        }

        private sealed class PeerDebugState
        {
            public int ClaimedNetworkId;
            public int MappedNetworkId;
            public string ClaimedName = string.Empty;
            public string MappedName = string.Empty;
            public long ReceivedPackets;
            public long AcceptedPackets;
            public long DroppedPackets;
            public long UsedSamples;
            public long InterpSamples;
            public long PredictSamples;
            public long NoBufferSamples;
            public long StaleSamples;
            public long NoSampleSamples;
            public long LastRawModelTime;
            public long LastAlignedModelTime;
            public long LastNativeAtReceiveTime;
            public long LastSampleModelTime;
            public long LastNativeLatestTime;
            public long LastSharedLatestTime;
            public long LastNativeAgeMs;
            public long LastSharedAgeMs;
            public long LastStaleDeltaMs;
            public long LastPredictMs;
            public long ClockOffsetMs;
            public float MatchDistanceM;
            public string LastReason = "new";
            public long WindowReceived;
            public long WindowAccepted;
            public long WindowDropped;
            public long WindowUsed;
            public long WindowInterp;
            public long WindowPredict;
            public long WindowNoBuffer;
            public long WindowStale;
            public long WindowNoSample;
            public long WindowFresh;
            public long WindowAgeSamples;
            public long WindowNativeAgeSum;
            public long WindowSharedAgeSum;
            public long WindowAdvantageSum;
            public long WindowUsedAgeSamples;
            public long WindowUsedNativeAgeSum;
            public long WindowUsedSharedAgeSum;
            public long WindowUsedAdvantageSum;
            public float RecvPps;
            public float AcceptedPps;
            public float DropPps;
            public float UsedPps;
            public float StalePps;
            public float NoBufferPps;
            public float NoSamplePps;
            public float UsedPercent;
            public float FreshPercent;
            public float StalePercent;
            public float AvgNativeAgeMs;
            public float AvgSharedAgeMs;
            public float AvgAdvantageMs;
            public float AvgUsedNativeAgeMs;
            public float AvgUsedSharedAgeMs;
            public float AvgUsedAdvantageMs;
            public bool HasSharedTraffic => ReceivedPackets > 0L || AcceptedPackets > 0L || DroppedPackets > 0L || UsedSamples > 0L;
            public long SortScore => ReceivedPackets * 1000000L + AcceptedPackets * 1000L + UsedSamples;

            public PeerDebugState(int networkId)
            {
                ClaimedNetworkId = networkId;
                MappedNetworkId = networkId;
            }

            public string Format()
            {
                return PlayerLabel
                    + " r/a/u " + ReceivedPackets + "/" + AcceptedPackets + "/" + UsedSamples
                    + " i/p " + InterpSamples + "/" + PredictSamples
                    + " nb/st/ns " + NoBufferSamples + "/" + StaleSamples + "/" + NoSampleSamples
                    + " age n/s " + LastNativeAgeMs + "/" + LastSharedAgeMs
                    + " sd " + LastStaleDeltaMs
                    + " off " + ClockOffsetMs
                    + " map " + MatchDistanceM.ToString("F1") + "m"
                    + " " + LastReason;
            }

            public string FormatQuality()
            {
                return PlayerLabel + " q/s r/a/u "
                    + RecvPps.ToString("F1") + "/" + AcceptedPps.ToString("F1") + "/" + UsedPps.ToString("F1")
                    + " drop " + DropPps.ToString("F1")
                    + " use " + UsedPercent.ToString("F0") + "%"
                    + " fresh " + FreshPercent.ToString("F0") + "%"
                    + " stale " + StalePercent.ToString("F0") + "%"
                    + " avg n/s " + AvgNativeAgeMs.ToString("F0") + "/" + AvgSharedAgeMs.ToString("F0")
                    + " adv " + AvgAdvantageMs.ToString("F0") + "ms"
                    + " used " + AvgUsedNativeAgeMs.ToString("F0") + "/" + AvgUsedSharedAgeMs.ToString("F0")
                    + " uAdv " + AvgUsedAdvantageMs.ToString("F0") + "ms";
            }

            private string PlayerLabel
            {
                get
                {
                    if (!string.IsNullOrEmpty(ClaimedName)
                        && !string.IsNullOrEmpty(MappedName)
                        && !string.Equals(ClaimedName, MappedName, StringComparison.Ordinal))
                    {
                        return ClaimedName + "->" + MappedName;
                    }

                    if (!string.IsNullOrEmpty(MappedName)) return MappedName;
                    if (!string.IsNullOrEmpty(ClaimedName)) return ClaimedName;
                    return "id " + MappedNetworkId;
                }
            }

            public void UpdateRateSnapshot(float elapsed)
            {
                RecvPps = WindowReceived / elapsed;
                AcceptedPps = WindowAccepted / elapsed;
                DropPps = WindowDropped / elapsed;
                UsedPps = WindowUsed / elapsed;
                StalePps = WindowStale / elapsed;
                NoBufferPps = WindowNoBuffer / elapsed;
                NoSamplePps = WindowNoSample / elapsed;

                long actionable = WindowUsed + WindowStale + WindowNoSample;
                UsedPercent = actionable > 0L ? 100f * WindowUsed / actionable : 0f;
                FreshPercent = WindowAgeSamples > 0L ? 100f * WindowFresh / WindowAgeSamples : 0f;
                StalePercent = WindowAgeSamples > 0L ? 100f * WindowStale / WindowAgeSamples : 0f;
                AvgNativeAgeMs = WindowAgeSamples > 0L ? (float)WindowNativeAgeSum / WindowAgeSamples : 0f;
                AvgSharedAgeMs = WindowAgeSamples > 0L ? (float)WindowSharedAgeSum / WindowAgeSamples : 0f;
                AvgAdvantageMs = WindowAgeSamples > 0L ? (float)WindowAdvantageSum / WindowAgeSamples : 0f;
                AvgUsedNativeAgeMs = WindowUsedAgeSamples > 0L ? (float)WindowUsedNativeAgeSum / WindowUsedAgeSamples : 0f;
                AvgUsedSharedAgeMs = WindowUsedAgeSamples > 0L ? (float)WindowUsedSharedAgeSum / WindowUsedAgeSamples : 0f;
                AvgUsedAdvantageMs = WindowUsedAgeSamples > 0L ? (float)WindowUsedAdvantageSum / WindowUsedAgeSamples : 0f;

                WindowReceived = 0;
                WindowAccepted = 0;
                WindowDropped = 0;
                WindowUsed = 0;
                WindowInterp = 0;
                WindowPredict = 0;
                WindowNoBuffer = 0;
                WindowStale = 0;
                WindowNoSample = 0;
                WindowFresh = 0;
                WindowAgeSamples = 0;
                WindowNativeAgeSum = 0;
                WindowSharedAgeSum = 0;
                WindowAdvantageSum = 0;
                WindowUsedAgeSamples = 0;
                WindowUsedNativeAgeSum = 0;
                WindowUsedSharedAgeSum = 0;
                WindowUsedAdvantageSum = 0;
            }
        }
    }
}
