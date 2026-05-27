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
    // Entry point. Mirrors the NoDynamicCollisions / KSL ExampleMod pattern:
    // BaseMod-derived class, KSLMeta-tagged, Start/Update/OnDestroy lifecycle.
    //
    // The mod operates in two modes:
    //   • Vanilla-compat: Tier 1 patches only. Pure local mitigations. Active
    //     whenever the mod is loaded; works regardless of whether other peers
    //     have the mod.
    //   • Enhanced: Tier 2/3 features (input-history broadcast, contact
    //     arbitration). Activates only when peer-presence detects that every
    //     other player in the room also has NetcodeFix loaded. Drops back to
    //     Vanilla-compat the moment a vanilla player joins.
    //
    // The handshake itself runs over Kino.Sync — KSL's out-of-band P2P channel
    // — so the CarX server never sees our extra packets and cannot rate-limit
    // or reject them.
    [KSLMeta("NetcodeFix", "0.1.0", "NV-Davyd")]
    public sealed class NetcodeFixMod : BaseMod
    {
        internal const string Version = "0.1.0";

        private static readonly Harmony Harmony = new Harmony("com.netcodefix");
        private static bool _patched;
        private static string _status = "Starting.";

        private PeerPresence   _presence;
        private SettingsUI     _settingsUi;
        private HighRateSync   _highRateSync;
        private ContactArbiter _contactArbiter;

        // ── Lifecycle ──────────────────────────────────────────────────────
        private void Start()
        {
            try
            {
                Settings.Load();
                ApplyHarmonyPatches();
                _presence = new PeerPresence();
                _presence.Initialize();
                _highRateSync = new HighRateSync(_presence);
                _highRateSync.Initialize();
                _contactArbiter = new ContactArbiter(_presence);
                _contactArbiter.Initialize();
                _settingsUi = new SettingsUI(_presence, _highRateSync, _contactArbiter);

                _status = "Active. Tier 1 patches loaded.";
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
            _presence?.Tick();
            _highRateSync?.Tick();
            UploadRatePatch.Tick(_presence);

            // Reset cached pos-system reference on disconnect so we re-resolve
            // when rejoining a room.
            if (NetworkController.InstanceGame == null)
                HighRateSync.ResetCache();
        }

        // Tier 3's proximity-based collision detection runs at physics rate.
        // BaseMod inherits MonoBehaviour, so Unity wires this up naturally.
        private void FixedUpdate()
        {
            _contactArbiter?.FixedTick();
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
                _contactArbiter?.Shutdown();
                _highRateSync?.Shutdown();
                _presence?.Shutdown();
                Settings.Save();
                _status = "Unloaded.";
                Kino.Log.Info("[NetcodeFix] Unloaded.");
            }
            catch (Exception ex)
            {
                Kino.Log.Error("[NetcodeFix] Unload failed: " + ex);
            }
        }

        // ── UI ─────────────────────────────────────────────────────────────
        public override void OnUIDraw()
        {
            Kino.UI.Label("Status: " + _status);
            Kino.UI.HorizontalLine();
            _settingsUi?.Draw();
        }

        public override void OnAdditionalAboutUIDraw()
        {
            Kino.UI.Label("Improves the CarX multiplayer netcode for close-quarters drift tandems.");
            Kino.UI.Label("Tier 1 patches (smoothing, snap, sanity filter) work against any peer.");
            Kino.UI.Label("Tier 2/3 features (input replay, contact arbitration) activate when all peers run NetcodeFix.");
        }

        public override Texture2D GetIcon()
        {
            return Kino.Utils.LoadEmbeddedTexture(Assembly.GetExecutingAssembly(), "NetcodeFix.Resources.icon.png");
        }

        // ── Patch application ──────────────────────────────────────────────
        private static void ApplyHarmonyPatches()
        {
            if (_patched) return;

            // Tier 1: rewrite PosSyncInfo.Update smoothing/snap path.
            Harmony.Patch(
                AccessTools.Method(typeof(SyncMultiplayer.NetGameSyncPosSystem.PosSyncInfo), "Update"),
                prefix: new HarmonyMethod(typeof(PosSyncInfoPatch), nameof(PosSyncInfoPatch.UpdatePrefix)));

            // Tier 1: validate incoming frames before they enter the interpolator.
            Harmony.Patch(
                AccessTools.Method(typeof(SyncMultiplayer.NetGameCircularInterpolator), "AddData"),
                prefix: new HarmonyMethod(typeof(InterpolatorPatch), nameof(InterpolatorPatch.AddDataPrefix)));

            _patched = true;
            Kino.Log.Info("[NetcodeFix] Harmony patches installed.");
        }
    }
}
