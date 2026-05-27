using System;
using System.Collections.Generic;
using GameOverlay;
using KSL.API;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Net
{
    // Periodically broadcasts a "I'm running NetcodeFix v<X>" announcement via
    // Kino.Sync. Tracks which other players in the room have responded — once
    // every visible peer has announced, the room is "all-modded" and Tier 2/3
    // features can light up. If any peer is missing or times out, fall back.
    //
    // Modeled after NoDynCollisions' presence pattern, with the addition of:
    //   • Capability flags (bitset of which optional protocols this peer
    //     supports). Lets us upgrade Tier 2 / 3 independently without
    //     breaking older peers.
    //   • Schema version (for future protocol breaks).
    //
    // The broadcast itself is small and infrequent (every 5s). It's NOT a
    // chat channel — only for handshake.
    public sealed class PeerPresence
    {
        private const string PacketName = "netcodefix_presence";
        private const float  BroadcastIntervalSecs = 5f;
        private const float  StaleTimeoutSecs      = 20f;
        private const int    SchemaVersion         = 1;

        // ── Capability flags ───────────────────────────────────────────────
        // OR'd into a uint32 sent in every presence packet. Receivers
        // intersect their own caps with the peers' caps to decide what
        // features the room can use.
        [Flags]
        public enum Caps : uint
        {
            None             = 0,
            Tier1Smoothing   = 1u << 0,
            SanityFilter     = 1u << 1,
            InputHistory     = 1u << 2,  // Tier 2 — not yet implemented
            UploadRateBoost  = 1u << 3,  // Tier 2 — not yet implemented
            ContactArbiter   = 1u << 4,  // Tier 3 — not yet implemented
        }

        // What WE support. Tier 1 caps are unconditional (the patches are
        // installed at Start and can't be disabled mid-session without an
        // unload). Tier 2/3 caps depend on whether the user has those
        // features turned on in settings — so we'd lie to peers if we
        // advertised them when they're off. Property, not constant.
        private static Caps LocalCaps
        {
            get
            {
                Caps c = Caps.Tier1Smoothing | Caps.SanityFilter;
                if (Settings.Current.tier2InputHistory)    c |= Caps.InputHistory;
                if (Settings.Current.tier2UploadRateBoost) c |= Caps.UploadRateBoost;
                if (Settings.Current.tier3ContactArbitration) c |= Caps.ContactArbiter;
                return c;
            }
        }

        private bool  _ready;
        private int   _packetId = -1;
        private float _nextBroadcast;

        private readonly Dictionary<string, Peer> _peers = new Dictionary<string, Peer>();

        public IReadOnlyDictionary<string, Peer> Peers => _peers;

        // Caps that EVERY currently-visible peer supports. Returned for
        // feature-gating: `if ((presence.RoomCaps & Caps.ContactArbiter) != 0) ...`
        // If no peers are visible, returns LocalCaps (single-player == all caps).
        public Caps RoomCaps
        {
            get
            {
                if (_peers.Count == 0) return LocalCaps;
                Caps c = LocalCaps;
                foreach (var p in _peers.Values) c &= p.Capabilities;
                return c;
            }
        }

        public int RemoteModdedCount => _peers.Count;

        public void Initialize()
        {
            if (!Kino.Sync.IsAvailable)
            {
                Kino.Log.Warning("[NetcodeFix] Kino.Sync unavailable; peer presence disabled.");
                return;
            }
            if (!Kino.Sync.TryRegisterPacket(PacketName, out _packetId))
            {
                Kino.Log.Warning("[NetcodeFix] Could not register presence packet.");
                return;
            }
            if (!Kino.Sync.SetCallback(_packetId, OnReceive))
            {
                Kino.Log.Warning("[NetcodeFix] Could not register presence callback.");
                return;
            }
            _ready = true;
            _nextBroadcast = 0f; // send immediately on first Tick
            Kino.Log.Info("[NetcodeFix] Peer presence sync ready (packetId=" + _packetId + ")");
        }

        public void Tick()
        {
            if (!_ready) return;

            // Periodic broadcast
            if (Time.unscaledTime >= _nextBroadcast)
            {
                Broadcast();
                _nextBroadcast = Time.unscaledTime + BroadcastIntervalSecs;
            }

            // Stale peer cleanup
            if (_peers.Count > 0)
            {
                float now = Time.unscaledTime;
                List<string> stale = null;
                foreach (var kv in _peers)
                {
                    if (now - kv.Value.LastSeen > StaleTimeoutSecs)
                    {
                        (stale ??= new List<string>()).Add(kv.Key);
                    }
                }
                if (stale != null) foreach (var id in stale) _peers.Remove(id);
            }
        }

        public void Shutdown()
        {
            _peers.Clear();
            _ready = false;
        }

        // ── Broadcast ──────────────────────────────────────────────────────
        private void Broadcast()
        {
            try
            {
                var p = Kino.Sync.CreatePacket(_packetId);
                p.WriteInt32(SchemaVersion);
                p.WriteString(NetcodeFixMod.Version);
                p.WriteUInt32((uint)LocalCaps);
                p.WriteString(GetLocalPlayerId());
                p.WriteString(GetLocalPlayerName());
                Kino.Sync.SendToAll(p);
            }
            catch (Exception ex)
            {
                Kino.Log.Warning("[NetcodeFix] Presence broadcast failed: " + ex.Message);
            }
        }

        // ── Receive ────────────────────────────────────────────────────────
        private void OnReceive(IPacket packet)
        {
            if (packet.Id != _packetId) return;
            try
            {
                int schema = packet.ReadInt32();
                if (schema != SchemaVersion)
                {
                    // Future protocol break — log once, then ignore the peer.
                    // Could track these separately for "outdated peers" warning.
                    return;
                }
                string version = packet.ReadString();
                Caps   caps    = (Caps)packet.ReadUInt32();
                string id      = packet.ReadString();
                string name    = packet.ReadString();

                if (string.IsNullOrEmpty(id) || id == GetLocalPlayerId()) return;

                _peers[id] = new Peer
                {
                    PlayerId     = id,
                    Name         = string.IsNullOrEmpty(name) ? id : name,
                    Version      = string.IsNullOrEmpty(version) ? "?" : version,
                    Capabilities = caps,
                    LastSeen     = Time.unscaledTime,
                };
            }
            catch (Exception ex)
            {
                Kino.Log.Warning("[NetcodeFix] Presence read failed: " + ex.Message);
            }
        }

        // ── Identity helpers (mirror NoDynamicCollisions' approach) ────────
        private static string GetLocalPlayerId()
        {
            try
            {
                var lp = NetworkController.InstanceGame?.LocalPlayer;
                if (lp != null && !lp.PlayerId.IsInvalid) return lp.PlayerId.ToString();
                if (Overlay.instance?.player != null && !Overlay.playerId.IsInvalid)
                    return Overlay.playerId.ToString();
            }
            catch { }
            ulong steamId = Kino.Info.SteamId;
            return steamId == 0UL ? "unknown" : steamId.ToString();
        }

        private static string GetLocalPlayerName()
        {
            try
            {
                var lp = NetworkController.InstanceGame?.LocalPlayer;
                if (lp != null && !string.IsNullOrEmpty(lp.FilteredNickName)) return lp.FilteredNickName;
                string n = Overlay.instance?.player?.nickName;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return GetLocalPlayerId();
        }

        // ── Peer record ────────────────────────────────────────────────────
        public sealed class Peer
        {
            public string PlayerId;
            public string Name;
            public string Version;
            public Caps   Capabilities;
            public float  LastSeen;
        }
    }
}
