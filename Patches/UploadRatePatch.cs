using System.Reflection;
using HarmonyLib;
using KSL.API;
using NetcodeFix.Net;
using SyncMultiplayer;

namespace NetcodeFix.Patches
{
    // Boosts the game's own NetGameSyncPosSystem.m_syncPlayer rate when the
    // room is verified all-modded. Vanilla = 33ms (~30Hz). We bump it to
    // 20ms (~50Hz) so our local car's vanilla packets land tighter for
    // every peer — including their own remote-extrapolation correction.
    //
    // We do NOT bump this in vanilla rooms — extra traffic for no benefit,
    // and the server might rate-limit. Gating is via PeerPresence.RoomCaps
    // intersect against the UploadRateBoost cap.
    //
    // ApproxTimeKeeper has a public `intervalMs` setter, so we just swap
    // the integer on the live instance. Re-asserted every Tick because
    // some other code could plausibly reset it on reconnect.
    internal static class UploadRatePatch
    {
        private const long VanillaIntervalMs = 33L;
        private const long BoostedIntervalMs = 20L;

        // Cached reflection on the private m_syncPlayer field.
        private static readonly FieldInfo F_syncPlayer =
            AccessTools.Field(typeof(NetGameSyncPosSystem), "m_syncPlayer");

        // What we last set the interval to, so we can detect "nothing to do".
        private static long _appliedInterval = -1;

        public static void Tick(PeerPresence presence)
        {
            if (F_syncPlayer == null) return;

            bool shouldBoost = Settings.Current.enabled
                            && Settings.Current.tier2UploadRateBoost
                            && presence.RemoteModdedCount > 0
                            && (presence.RoomCaps & PeerPresence.Caps.UploadRateBoost) != 0;

            long target = shouldBoost ? BoostedIntervalMs : VanillaIntervalMs;
            if (_appliedInterval == target) return;

            // Find the pos system instance. Same lookup HighRateSync uses;
            // cached internally so subsequent calls are cheap.
            var posSystem = HighRateSync_FindPosSystem();
            if (posSystem == null) return;

            try
            {
                var atk = F_syncPlayer.GetValue(posSystem) as ApproxTimeKeeper;
                if (atk == null) return;
                atk.intervalMs = target;
                _appliedInterval = target;
                Kino.Log.Info("[NetcodeFix] Upload-rate interval set to " + target + "ms");
            }
            catch (System.Exception ex)
            {
                Kino.Log.Warning("[NetcodeFix] UploadRatePatch failed: " + ex.Message);
            }
        }

        // Tiny wrapper so we don't need to expose HighRateSync's private
        // cache field. Re-implements the same lookup with the same fallback
        // logic — kept in sync with HighRateSync.FindPosSystem manually.
        private static NetGameSyncPosSystem HighRateSync_FindPosSystem()
        {
            try
            {
                var game = NetworkController.InstanceGame;
                if (game == null) return null;
                var systemsField = AccessTools.Field(game.GetType(), "m_systems")
                                   ?? AccessTools.Field(game.GetType(), "systems")
                                   ?? AccessTools.Field(game.GetType(), "SystemsManager");
                if (systemsField == null) return null;
                var systemsObj = systemsField.GetValue(game);
                if (systemsObj == null) return null;
                var getMethod = systemsObj.GetType().GetMethod("Get")?.MakeGenericMethod(typeof(NetGameSyncPosSystem));
                return getMethod?.Invoke(systemsObj, null) as NetGameSyncPosSystem;
            }
            catch { return null; }
        }
    }
}
