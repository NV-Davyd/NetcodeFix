using System;
using System.Reflection;
using HarmonyLib;
using SyncMultiplayer;

namespace NetcodeFix.Patches
{
    internal static class NativeSyncRatePatch
    {
        private const long StockIntervalMs = 33L;

        private static readonly FieldInfo F_syncPlayer =
            AccessTools.Field(typeof(NetGameSyncPosSystem), "m_syncPlayer");

        public static void UpdatePrefix(NetGameSyncPosSystem __instance)
        {
            ApplyInterval(__instance, GetTargetIntervalMs());
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
            float hz = Math.Max(30f, Math.Min(60f, Settings.NativeSendRateHz));
            return Math.Max(1L, (long)Math.Round(1000.0 / hz));
        }

        private static void ApplyInterval(NetGameSyncPosSystem syncPos, long intervalMs)
        {
            if (syncPos == null || F_syncPlayer == null)
            {
                return;
            }

            ApproxTimeKeeper keeper = F_syncPlayer.GetValue(syncPos) as ApproxTimeKeeper;
            if (keeper == null)
            {
                return;
            }

            if (keeper.intervalMs != intervalMs)
            {
                keeper.intervalMs = intervalMs;
                keeper.ResetToNow();
            }
        }
    }
}
