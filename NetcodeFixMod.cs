using System;
using System.Reflection;
using HarmonyLib;
using KSL.API;
using NetcodeFix.Net;
using NetcodeFix.Patches;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix
{
    [KSLMeta("NetcodeFix", "0.6.3", "Envy")]
    public sealed class NetcodeFixMod : BaseMod
    {
        internal const string Version = "0.6.3";

        private static readonly Harmony Harmony = new Harmony("com.envy.carx.netcodefix");
        private static bool _patched;
        private static string _status = "Starting.";

        private void Start()
        {
            try
            {
                Settings.Load();
                ApplyHarmonyPatches();
                SharedPoseSync.Initialize();
                _status = "Active.";
                Kino.Log.Info("[NetcodeFix] Loaded v" + Version);
            }
            catch (Exception ex)
            {
                _status = "Load failed: " + ex.Message;
                Kino.Log.Error("[NetcodeFix] Load failed: " + ex);
            }
        }

        private void Update()
        {
            SharedPoseSync.Tick();
        }

        private void FixedUpdate()
        {
            SharedPoseSync.FixedTick();
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

                SharedPoseSync.Shutdown();
                NativeSyncRatePatch.RestoreCurrent();
                Settings.Save();
                _status = "Unloaded.";
                Kino.Log.Info("[NetcodeFix] Unloaded.");
            }
            catch (Exception ex)
            {
                Kino.Log.Error("[NetcodeFix] Unload failed: " + ex);
            }
        }

        public override void OnUIDraw()
        {
            Kino.UI.Label("Status: " + _status);
            Kino.UI.Label("Shared poses: sent " + SharedPoseSync.FramesSent
                + " / burst " + SharedPoseSync.BurstFramesSent
                + " / recv " + SharedPoseSync.FramesReceived
                + " / used " + SharedPoseSync.FramesUsed
                + " / drop " + SharedPoseSync.FramesDropped);
            Kino.UI.HorizontalLine();
            SettingsUI.Draw();
        }

        public override void OnAdditionalAboutUIDraw()
        {
            Kino.UI.Label("Tightens CarX remote-car sync for tandem rooms.");
            Kino.UI.Label("Uses native send-rate patching, interpolation fixes, and diagnostics.");
        }

        public override Texture2D GetIcon()
        {
            return Kino.Utils.LoadEmbeddedTexture(Assembly.GetExecutingAssembly(), "NetcodeFix.Resources.icon.png");
        }

        private static void ApplyHarmonyPatches()
        {
            if (_patched) return;

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameCircularInterpolator), nameof(NetGameCircularInterpolator.GetDataFrame)),
                prefix: new HarmonyMethod(typeof(InterpolatorPatch), nameof(InterpolatorPatch.GetDataFramePrefix)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameCircularInterpolator), "Interpolate"),
                prefix: new HarmonyMethod(typeof(InterpolatorPatch), nameof(InterpolatorPatch.InterpolatePrefix)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameSyncPosSystem.PosSyncInfo), "Update"),
                prefix: new HarmonyMethod(typeof(PosSyncInfoPatch), nameof(PosSyncInfoPatch.UpdatePrefix)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameSyncPosSystem.PosSyncInfo), "Sync"),
                transpiler: new HarmonyMethod(typeof(PosSyncInfoPatch), nameof(PosSyncInfoPatch.SyncTranspiler)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameSyncPosSystem), nameof(NetGameSyncPosSystem.Update)),
                prefix: new HarmonyMethod(typeof(NativeSyncRatePatch), nameof(NativeSyncRatePatch.UpdatePrefix)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetworkGame), nameof(NetworkGame.SendRealtimeData), new[] { typeof(SmartfoxDataPackage) }),
                prefix: new HarmonyMethod(typeof(NativeSyncRatePatch), nameof(NativeSyncRatePatch.SendRealtimeDataPrefix)));

            Harmony.Patch(
                AccessTools.Method(typeof(NetGameCircularInterpolator), nameof(NetGameCircularInterpolator.AddData)),
                prefix: new HarmonyMethod(typeof(NativeSyncRatePatch), nameof(NativeSyncRatePatch.AddDataPrefix)));

            _patched = true;
            Kino.Log.Info("[NetcodeFix] Harmony patches installed.");
        }
    }
}
