using System;
using System.Reflection;
using HarmonyLib;
using KSL.API;
using NetcodeFix.Patches;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix
{
    [KSLMeta("NetcodeFix", "1.1.0", "Envy")]
    public sealed class NetcodeFixMod : BaseMod
    {
        internal const string Version = "1.1.0";

        private static readonly Harmony Harmony = new Harmony("com.envy.carx.netcodefix");
        private static bool _patched;

        private void Start()
        {
            try
            {
                Settings.Load();
                SyncCorrectionOverlay.Initialize();
                ApplyHarmonyPatches();
                PresenceSync.Initialize();
                Kino.Log.Info("[NetcodeFix] Loaded v" + Version);
            }
            catch (Exception ex)
            {
                Kino.Log.Error("[NetcodeFix] Load failed: " + ex);
            }
        }

        private void Update()
        {
            PresenceSync.Tick();
            SyncCorrectionOverlay.Tick();
        }

        private void OnDestroy()
        {
            try
            {
                if (_patched)
                {
                    Harmony.UnpatchSelf();
                    _patched = false;
                }

                PresenceSync.Shutdown();
                SyncCorrectionOverlay.Shutdown();
                NativeSyncRatePatch.RestoreCurrent();
                Settings.Save();
                Kino.Log.Info("[NetcodeFix] Unloaded.");
            }
            catch (Exception ex)
            {
                Kino.Log.Error("[NetcodeFix] Unload failed: " + ex);
            }
        }

        public override void OnUIDraw()
        {
            SettingsUI.Draw();
        }

        public override void OnAdditionalAboutUIDraw()
        {
            Kino.UI.Label("What does this mod do?");
            Kino.UI.Label("");
            Kino.UI.Label("- prevents CarX's debug future-position sample from overwriting the real extrapolation state");
            Kino.UI.Label("- fixes fractional interpolation between sync frames");
            Kino.UI.Label("- adjusts how strongly remote cars are pulled toward their latest synced position");
            Kino.UI.Label("- raises CarX's native realtime sync cadence within the game's multiplayer limits");
        }

        public override Texture2D GetIcon()
        {
            return Kino.Utils.LoadEmbeddedTexture(Assembly.GetExecutingAssembly(), "NetcodeFix.Resources.icon.png");
        }

        private static void ApplyHarmonyPatches()
        {
            if (_patched) return;

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameCircularInterpolator), "Interpolate"),
                prefix: new HarmonyMethod(typeof(InterpolatorPatch), nameof(InterpolatorPatch.InterpolatePrefix)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameCircularInterpolator), "Extrapolate"),
                postfix: new HarmonyMethod(typeof(InterpolatorPatch), nameof(InterpolatorPatch.ExtrapolatePostfix)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameCircularInterpolator), "ExtrapolateLast"),
                prefix: new HarmonyMethod(typeof(StaleExtrapolationPatch), nameof(StaleExtrapolationPatch.ExtrapolateLastPrefix)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameSyncPosSystem.PosSyncInfo), "Update"),
                prefix: new HarmonyMethod(typeof(PosSyncInfoPatch), nameof(PosSyncInfoPatch.UpdatePrefix)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameSyncPosSystem.PosSyncInfo), "Sync"),
                transpiler: new HarmonyMethod(typeof(PosSyncInfoPatch), nameof(PosSyncInfoPatch.SyncTranspiler)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameSyncPosSystem), nameof(NetGameSyncPosSystem.Update)),
                prefix: new HarmonyMethod(typeof(NativeSyncRatePatch), nameof(NativeSyncRatePatch.UpdatePrefix)));

            _patched = true;
            Kino.Log.Info("[NetcodeFix] Harmony patches installed.");
        }
    }
}
