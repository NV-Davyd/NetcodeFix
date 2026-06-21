using System;
using System.Reflection;
using HarmonyLib;
using KSL.API;
using NetcodeFix.Patches;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix
{
    [KSLMeta("NetcodeFix", "1.0.3", "Envy")]
    public sealed class NetcodeFixMod : BaseMod
    {
        internal const string Version = "1.0.3";

        private static readonly Harmony Harmony = new Harmony("com.envy.carx.netcodefix");
        private static bool _patched;
        private static GUIStyle _noticeTitleStyle;
        private static GUIStyle _noticeBodyStyle;
        private static GUIStyle _noticeValueStyle;
        private static GUIStyle _noticeButtonStyle;

        private void Start()
        {
            try
            {
                Settings.Load();
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

        private void OnGUI()
        {
            if (!Settings.ShowDefaultResetNotice) return;

            EnsureNoticeStyles();

            float width = Mathf.Min(560f, Screen.width - 40f);
            Rect rect = new Rect((Screen.width - width) * 0.5f, Mathf.Max(32f, Screen.height * 0.14f), width, 280f);
            Rect contentRect = new Rect(rect.x + 24f, rect.y + 22f, rect.width - 48f, rect.height - 44f);

            DrawRect(new Rect(rect.x + 4f, rect.y + 5f, rect.width, rect.height), new Color(0f, 0f, 0f, 0.30f));
            DrawRect(rect, new Color(0.06f, 0.07f, 0.08f, 0.96f));
            DrawBorder(rect, new Color(0.35f, 0.60f, 0.82f, 0.95f), 2f);
            DrawRect(new Rect(rect.x + 2f, rect.y + 2f, rect.width - 4f, 3f), new Color(0.31f, 0.68f, 1f, 1f));

            GUI.Label(new Rect(contentRect.x, contentRect.y, contentRect.width, 30f), "NetcodeFix updated", _noticeTitleStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 48f, contentRect.width, 22f), "Correction rate has been reset to", _noticeBodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 76f, contentRect.width, 38f), "1.00", _noticeValueStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 120f, contentRect.width, 22f), "so the mod does not change game feel by default.", _noticeBodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 158f, contentRect.width, 22f), "To tune it, open KSL -> NetcodeFix -> Debug.", _noticeBodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 184f, contentRect.width, 22f), "Otherwise, leave it at 1.00.", _noticeBodyStyle);

            Rect buttonRect = new Rect(rect.xMax - 134f, rect.yMax - 52f, 110f, 34f);
            if (GUI.Button(buttonRect, "OK", _noticeButtonStyle))
            {
                Settings.ShowDefaultResetNotice = false;
                Settings.Save();
            }
        }

        private static void EnsureNoticeStyles()
        {
            if (_noticeTitleStyle != null) return;

            Texture2D buttonTexture = MakeTexture(new Color(0.18f, 0.55f, 0.86f, 1f));
            Texture2D buttonHoverTexture = MakeTexture(new Color(0.24f, 0.63f, 0.94f, 1f));
            Texture2D buttonActiveTexture = MakeTexture(new Color(0.11f, 0.42f, 0.70f, 1f));

            _noticeTitleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _noticeBodyStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 15,
                wordWrap = true,
                normal = { textColor = new Color(0.86f, 0.90f, 0.94f, 1f) }
            };
            _noticeValueStyle = new GUIStyle(_noticeTitleStyle)
            {
                fontSize = 30,
                normal = { textColor = new Color(0.52f, 0.80f, 1f, 1f) }
            };
            _noticeButtonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { background = buttonTexture, textColor = Color.white },
                hover = { background = buttonHoverTexture, textColor = Color.white },
                active = { background = buttonActiveTexture, textColor = Color.white }
            };
        }

        private static void DrawBorder(Rect rect, Color color, float width)
        {
            DrawRect(new Rect(rect.x, rect.y, rect.width, width), color);
            DrawRect(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
            DrawRect(new Rect(rect.x, rect.y, width, rect.height), color);
            DrawRect(new Rect(rect.xMax - width, rect.y, width, rect.height), color);
        }

        private static void DrawRect(Rect rect, Color color)
        {
            Color oldColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = oldColor;
        }

        private static Texture2D MakeTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
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
