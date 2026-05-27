using System;
using System.Reflection;
using CarX;
using HarmonyLib;
using KSL.API;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Net
{
    // Tier 2 — supplemental high-rate pose sync between modded peers.
    //
    // We do NOT replace the game's SmartFox sync. We BROADCAST extra frames
    // at ~50Hz via Kino.Sync (KSL's out-of-band P2P channel), and on the
    // receiving side we feed them into the SAME interpolator buffer the
    // game's vanilla packets use. The game's existing extrapolation /
    // interpolation logic then has roughly 2-2.5× the data density to work
    // with — the blind-spot between vanilla packets gets filled in.
    //
    // Gates:
    //   • Only active when at least one other modded peer is visible
    //     (otherwise we're just wasting bandwidth).
    //   • Feature can be toggled by the user; presence advertises the
    //     InputHistory capability bit only while running.
    //
    // Bandwidth: ~110 bytes/frame × 50Hz × N peers receiving = N × ~5.5 KB/s
    // per modded client. In a full 16-player tournament that's ~90 KB/s,
    // roughly doubling the vanilla netcode load. Acceptable on broadband.
    public sealed class HighRateSync
    {
        private const string PacketName = "netcodefix_pose";

        // 20ms = 50Hz. Below 16ms (~60Hz+) we'd contend with the next
        // FixedUpdate; above 33ms we wouldn't actually help vs the game's
        // 30Hz. 50Hz is the sweet spot.
        private const float SendIntervalSecs = 0.020f;

        private readonly PeerPresence _presence;
        private int   _packetId = -1;
        private bool  _ready;
        private float _nextSend;
        private long  _framesSent;
        private long  _framesReceived;
        private long  _framesInjected;
        private long  _framesDropped;

        public bool   IsActive     => _ready && Settings.Current.tier2InputHistory;
        public long   FramesSent   => _framesSent;
        public long   FramesInject => _framesInjected;
        public long   FramesDrop   => _framesDropped;

        // Reflection — find NetGameSyncPosSystem instance + private fields
        // for fast per-frame access during inject path.
        private static FieldInfo _syncInfosField;
        // NetGameSyncPosSystem.m_syncTime is `private static`. Read via
        // reflection because the field is invisible to consumers.
        private static FieldInfo _syncTimeField;

        public HighRateSync(PeerPresence presence)
        {
            _presence = presence;
        }

        public void Initialize()
        {
            if (!Kino.Sync.IsAvailable)
            {
                Kino.Log.Warning("[NetcodeFix] Kino.Sync unavailable; high-rate sync disabled.");
                return;
            }
            if (!Kino.Sync.TryRegisterPacket(PacketName, out _packetId))
            {
                Kino.Log.Warning("[NetcodeFix] Could not register high-rate sync packet.");
                return;
            }
            if (!Kino.Sync.SetCallback(_packetId, OnReceive))
            {
                Kino.Log.Warning("[NetcodeFix] Could not register high-rate sync callback.");
                return;
            }
            _syncInfosField = AccessTools.Field(typeof(NetGameSyncPosSystem), "m_SyncInfos");
            _syncTimeField  = AccessTools.Field(typeof(NetGameSyncPosSystem), "m_syncTime");
            _ready = true;
            Kino.Log.Info("[NetcodeFix] High-rate pose sync ready (packetId=" + _packetId + ")");
        }

        public void Tick()
        {
            if (!IsActive) return;

            // Only broadcast if at least one modded peer is in the room and
            // they advertise the InputHistory cap (i.e. they'll actually
            // consume our packets).
            if (_presence.RemoteModdedCount == 0) return;
            if ((_presence.RoomCaps & PeerPresence.Caps.InputHistory) == 0) return;

            if (Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + SendIntervalSecs;

            BroadcastLocalPose();
        }

        public void Shutdown()
        {
            _ready = false;
        }

        // ── Broadcast ──────────────────────────────────────────────────────
        private void BroadcastLocalPose()
        {
            try
            {
                var game = NetworkController.InstanceGame;
                var localPlayer = game?.LocalPlayer;
                if (localPlayer?.userCar == null) return;
                var raceCar = localPlayer.userCar;
                if (raceCar.carX == null || raceCar.carX.getRigidbody == null) return;

                // Build a frame using the same fields the game's
                // GetCurrentFromCar() reads — so our supplemental frames
                // are indistinguishable from the game's own captures.
                PosDataFrame frame = PosDataFrame.Default;
                BuildLocalFrame(ref frame, raceCar, localPlayer);

                var packet = Kino.Sync.CreatePacket(_packetId);
                SyncPacketCodec.WritePoseFrame(packet, localPlayer.NetworkID, ref frame);
                Kino.Sync.SendToAll(packet);
                _framesSent++;
            }
            catch (Exception ex)
            {
                Kino.Log.Warning("[NetcodeFix] BroadcastLocalPose failed: " + ex.Message);
            }
        }

        private static void BuildLocalFrame(ref PosDataFrame frame, RaceCar car, NetworkPlayer player)
        {
            Car carX = car.carX;
            Rigidbody rb = carX.getRigidbody;

            // Match the game's NetGameSyncTimeSystem.GetModelFixedTimeMs() time
            // base so our frames slot into the interpolator in the right place.
            // The m_syncTime field is private static — pull via reflection.
            var syncTime = _syncTimeField?.GetValue(null) as NetGameSyncTimeSystem;
            frame.modelTime = syncTime != null
                ? syncTime.GetModelFixedTimeMs()
                : (long)(Time.fixedTime * 1000f);

            frame.posGeneration = player.spawnGeneration;
            frame.flags         = DataFrameFlags.None;

            frame.accelerate = carX.accelerate;
            frame.brake      = carX.brake;
            frame.handbrake  = carX.handbrake;
            frame.clutch     = carX.clutch;
            frame.steer      = carX.GetSteer();
            frame.gear       = (byte)carX.gear;
            frame.rpm        = carX.rpm;
            frame.flares     = car.flares != null && car.flares.flares != null && car.flares.flares.activeSelf;

            frame.processEngineStart = true;
            frame.ignitionEnabled    = car.engineController != null && car.engineController.isIgnitionOn;
            frame.starterEnabled     = car.engineController != null && car.engineController.isStarterOn;
            frame.engineStartTime    = 0.75f;
            frame.autoClutch         = carX.autoClutch;
            frame.clutchKick         = car.isClutchKick;

            frame.position        = rb.position;
            frame.rotation        = rb.rotation;
            frame.velocity        = rb.velocity;
            frame.angularVelocity = rb.angularVelocity;

            frame.longAccel = carX.longAccel;
            frame.sideAccel = carX.sideAccel;
            frame.angAccel  = carX.angAccel;
            frame.mileage   = car.mileageMoreZero;
        }

        // ── Receive ────────────────────────────────────────────────────────
        private void OnReceive(IPacket packet)
        {
            if (packet.Id != _packetId) return;
            _framesReceived++;

            // Even if the feature is "off" locally, accept frames — the
            // remote side might still be broadcasting. The IsActive check
            // gates BROADCAST, not RECEIVE.
            if (!_ready) { _framesDropped++; return; }

            try
            {
                if (!SyncPacketCodec.TryReadPoseFrame(packet, out int senderId, out var frame))
                {
                    _framesDropped++;
                    return;
                }

                if (!InjectIntoInterpolator(senderId, ref frame))
                    _framesDropped++;
                else
                    _framesInjected++;
            }
            catch (Exception ex)
            {
                _framesDropped++;
                Kino.Log.Warning("[NetcodeFix] HighRateSync OnReceive failed: " + ex.Message);
            }
        }

        // Finds the sender's PosSyncInfo by their NetworkID, then feeds the
        // received frame into their interpolator buffer. Same code path the
        // game's HandleAsync uses, just from a different packet source.
        private static bool InjectIntoInterpolator(int senderNetworkId, ref PosDataFrame frame)
        {
            if (_syncInfosField == null) return false;

            // Find the NetGameSyncPosSystem instance via the live game's
            // systems manager. We can't easily cache this because it can
            // be torn down/recreated on disconnect/reconnect.
            var posSystem = FindPosSystem();
            if (posSystem == null) return false;

            // Use the public TryGetRAWInfo accessor — avoids one more
            // reflection hop and matches what the game does internally.
            if (!posSystem.TryGetRAWInfo(senderNetworkId, out var posInfo)) return false;
            if (posInfo?.interpolator == null) return false;

            // Mark as not-extrapolated origin so smoothing logic treats
            // it like any other authoritative frame.
            posInfo.interpolator.AddData(ref frame);
            posInfo.lastTimeUpdated = frame.modelTime;
            posInfo.isSyncDirty = true; // make the next sync pull from the new buffer state
            return true;
        }

        // Walk the network systems list to find NetGameSyncPosSystem.
        // Cached after first successful lookup to avoid the linear scan.
        private static NetGameSyncPosSystem _cachedPosSystem;
        private static NetGameSyncPosSystem FindPosSystem()
        {
            if (_cachedPosSystem != null) return _cachedPosSystem;

            try
            {
                // Walk via NetworkController → InstanceGame → SystemsManager
                var game = NetworkController.InstanceGame;
                if (game == null) return null;

                var systemsField = AccessTools.Field(game.GetType(), "m_systems")
                                   ?? AccessTools.Field(game.GetType(), "systems")
                                   ?? AccessTools.Field(game.GetType(), "SystemsManager");
                if (systemsField == null) return null;
                var systemsObj = systemsField.GetValue(game);
                if (systemsObj == null) return null;

                // NetGameSystemsManager has a Get<T> method we can call.
                var getMethod = systemsObj.GetType().GetMethod("Get")?.MakeGenericMethod(typeof(NetGameSyncPosSystem));
                if (getMethod != null)
                {
                    _cachedPosSystem = getMethod.Invoke(systemsObj, null) as NetGameSyncPosSystem;
                }
            }
            catch { }

            return _cachedPosSystem;
        }

        // Clear cached reference on game disconnect so we re-resolve on
        // next room join. Called from NetcodeFixMod.Update via a coarse
        // check (InstanceGame == null → reset).
        public static void ResetCache()
        {
            _cachedPosSystem = null;
        }
    }
}
