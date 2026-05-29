using System;
using System.Reflection;
using HarmonyLib;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Patches
{
    internal static class NativeSyncRatePatch
    {
        private const long StockIntervalMs = 33L;

        private static readonly FieldInfo F_syncPlayer =
            AccessTools.Field(typeof(NetGameSyncPosSystem), "m_syncPlayer");

        private static readonly FieldInfo F_interpolatorHost =
            AccessTools.Field(typeof(NetGameCircularInterpolator), "m_host");

        private static float _rateWindowStart;
        private static float _nextRateUpdate;
        private static long _windowNativeSent;
        private static long _windowNativeReceived;

        public static string StatusLine = "Native sync: waiting for multiplayer.";
        public static string RateLine = "Native realtime: -";
        public static long NativeRealtimeSent;
        public static long NativeRealtimeReceived;

        public static void UpdatePrefix(NetGameSyncPosSystem __instance)
        {
            ApplyInterval(__instance, GetTargetIntervalMs());
            UpdateRateSnapshot();
        }

        public static void SendRealtimeDataPrefix()
        {
            NativeRealtimeSent++;
            _windowNativeSent++;
        }

        public static void AddDataPrefix(NetGameCircularInterpolator __instance)
        {
            try
            {
                NetworkPlayer host = F_interpolatorHost?.GetValue(__instance) as NetworkPlayer;
                if (host == null || host.IsLocal) return;

                NativeRealtimeReceived++;
                _windowNativeReceived++;
            }
            catch
            {
            }
        }

        public static void RestoreCurrent()
        {
            try
            {
                NetGameSyncPosSystem syncPos = NetworkController.InstanceGame?.systems?.Get<NetGameSyncPosSystem>();
                if (syncPos != null)
                {
                    ApplyInterval(syncPos, StockIntervalMs);
                }
            }
            catch
            {
            }
        }

        private static long GetTargetIntervalMs()
        {
            if (!Settings.Enabled || !Settings.OverrideNativeSendRate)
            {
                return StockIntervalMs;
            }

            float hz = Math.Max(30f, Math.Min(60f, Settings.NativeSendRateHz));
            return Math.Max(1L, (long)Math.Round(1000.0 / hz));
        }

        private static void ApplyInterval(NetGameSyncPosSystem syncPos, long intervalMs)
        {
            if (syncPos == null || F_syncPlayer == null)
            {
                StatusLine = "Native sync: unavailable.";
                return;
            }

            ApproxTimeKeeper keeper = F_syncPlayer.GetValue(syncPos) as ApproxTimeKeeper;
            if (keeper == null)
            {
                StatusLine = "Native sync: timer missing.";
                return;
            }

            if (keeper.intervalMs != intervalMs)
            {
                keeper.intervalMs = intervalMs;
                keeper.ResetToNow();
            }

            float hz = 1000f / Math.Max(1f, keeper.intervalMs);
            string mode = intervalMs == StockIntervalMs ? "stock" : "patched";
            StatusLine = "Native sync: " + mode + " " + hz.ToString("F0") + " Hz (" + keeper.intervalMs + " ms)";
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
            float sent = _windowNativeSent / elapsed;
            float received = _windowNativeReceived / elapsed;
            RateLine = "Native realtime: sent " + sent.ToString("F1")
                + "/s recv " + received.ToString("F1") + "/s"
                + " total " + NativeRealtimeSent + "/" + NativeRealtimeReceived;
            ResetRateWindow(now);
        }

        private static void ResetRateWindow(float now)
        {
            _rateWindowStart = now;
            _nextRateUpdate = now + 1f;
            _windowNativeSent = 0;
            _windowNativeReceived = 0;
        }
    }
}
