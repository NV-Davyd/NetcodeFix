using System;
using System.Collections.Generic;
using GameOverlay;
using KSL.API;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix
{
    internal static class PresenceSync
    {
        private const string PresencePacketName = "netcodefix_presence";
        private const float PresenceSendInterval = 5f;
        private const float PresenceTimeout = 20f;

        private static readonly Dictionary<string, PeerPresence> Peers = new Dictionary<string, PeerPresence>();
        private static bool _syncReady;
        private static int _presencePacketId = -1;
        private static float _nextPresenceSendTime;

        public static int PeerCount
        {
            get
            {
                RemoveStalePeers();
                return Peers.Count;
            }
        }

        public static void Initialize()
        {
            Peers.Clear();
            _nextPresenceSendTime = 0f;

            if (!Kino.Sync.IsAvailable)
            {
                _syncReady = false;
                Kino.Log.Warning("[NetcodeFix] KSL sync is unavailable; peer presence is disabled.");
                return;
            }

            if (!Kino.Sync.TryRegisterPacket(PresencePacketName, out _presencePacketId))
            {
                _syncReady = false;
                Kino.Log.Warning("[NetcodeFix] Unable to register presence packet.");
                return;
            }

            if (!Kino.Sync.SetCallback(_presencePacketId, OnPresencePacketReceived))
            {
                _syncReady = false;
                Kino.Log.Warning("[NetcodeFix] Unable to register presence callback.");
                return;
            }

            _syncReady = true;
            SendPresence();
            Kino.Log.Info("[NetcodeFix] Presence sync initialized.");
        }

        public static void Tick()
        {
            RemoveStalePeers();
            if (!_syncReady || Time.unscaledTime < _nextPresenceSendTime)
            {
                return;
            }

            SendPresence();
        }

        public static void Shutdown()
        {
            Peers.Clear();
            _syncReady = false;
        }

        public static void DrawPeers()
        {
            RemoveStalePeers();
            Kino.UI.Label("Other players with this mod: " + Peers.Count);

            foreach (PeerPresence peer in Peers.Values)
            {
                Kino.UI.Label(peer.Name + " v" + peer.Version);
            }
        }

        private static void SendPresence()
        {
            _nextPresenceSendTime = Time.unscaledTime + PresenceSendInterval;
            if (!_syncReady)
            {
                return;
            }

            try
            {
                IPacket packet = Kino.Sync.CreatePacket(_presencePacketId);
                packet.WriteString(GetLocalPlayerId());
                packet.WriteString(GetLocalPlayerName());
                packet.WriteString(NetcodeFixMod.Version);
                Kino.Sync.SendToAll(packet);
            }
            catch (Exception ex)
            {
                Kino.Log.Warning("[NetcodeFix] Presence broadcast failed: " + ex);
            }
        }

        private static void OnPresencePacketReceived(IPacket packet)
        {
            if (packet.Id != _presencePacketId)
            {
                return;
            }

            try
            {
                string playerId = packet.ReadString();
                string name = packet.ReadString();
                string version = packet.ReadString();
                if (string.IsNullOrEmpty(playerId) || playerId == GetLocalPlayerId())
                {
                    return;
                }

                Peers[playerId] = new PeerPresence
                {
                    PlayerId = playerId,
                    Name = string.IsNullOrEmpty(name) ? playerId : name,
                    Version = string.IsNullOrEmpty(version) ? "unknown" : version,
                    LastSeen = Time.unscaledTime
                };
            }
            catch (Exception ex)
            {
                Kino.Log.Warning("[NetcodeFix] Presence packet read failed: " + ex);
            }
        }

        private static void RemoveStalePeers()
        {
            float now = Time.unscaledTime;
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, PeerPresence> peer in Peers)
            {
                if (now - peer.Value.LastSeen > PresenceTimeout)
                {
                    stale.Add(peer.Key);
                }
            }

            foreach (string playerId in stale)
            {
                Peers.Remove(playerId);
            }
        }

        private static string GetLocalPlayerId()
        {
            try
            {
                NetworkPlayer localPlayer = NetworkController.InstanceGame?.LocalPlayer;
                if (localPlayer != null && !localPlayer.PlayerId.IsInvalid)
                {
                    return localPlayer.PlayerId.ToString();
                }

                if (Overlay.instance?.player != null && !Overlay.playerId.IsInvalid)
                {
                    return Overlay.playerId.ToString();
                }
            }
            catch
            {
            }

            ulong steamId = Kino.Info.SteamId;
            return steamId == 0UL ? "unknown" : steamId.ToString();
        }

        private static string GetLocalPlayerName()
        {
            try
            {
                NetworkPlayer localPlayer = NetworkController.InstanceGame?.LocalPlayer;
                if (localPlayer != null && !string.IsNullOrEmpty(localPlayer.FilteredNickName))
                {
                    return localPlayer.FilteredNickName;
                }

                string overlayName = Overlay.instance?.player?.nickName;
                if (!string.IsNullOrEmpty(overlayName))
                {
                    return overlayName;
                }
            }
            catch
            {
            }

            return GetLocalPlayerId();
        }

        private sealed class PeerPresence
        {
            public string PlayerId;
            public string Name;
            public string Version;
            public float LastSeen;
        }
    }
}
