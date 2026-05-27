using KSL.API;
using NetcodeFix.Net;

namespace NetcodeFix
{
    // Draws inside KSL's mod-settings page. We use Kino.UI primitives only;
    // no IMGUI windows. Slider signature is (ref value, min, max, format) —
    // no label arg — so each slider is preceded by an explicit Label().
    public sealed class SettingsUI
    {
        private readonly PeerPresence _presence;

        public SettingsUI(PeerPresence presence)
        {
            _presence = presence;
        }

        public void Draw()
        {
            var s = Settings.Current;

            // ── Master toggle ──────────────────────────────────────────────
            bool en = s.enabled;
            Kino.UI.Toggle("Enable NetcodeFix", ref en);
            if (en != s.enabled) { s.enabled = en; Settings.Save(); }

            Kino.UI.HorizontalLine();

            // ── Peer presence ──────────────────────────────────────────────
            Kino.UI.GroupLabel("Modded peers in room: " + _presence.RemoteModdedCount);
            foreach (var p in _presence.Peers.Values)
                Kino.UI.Label("  • " + p.Name + "  v" + p.Version);
            Kino.UI.Label("Room capabilities: " + _presence.RoomCaps);

            Kino.UI.HorizontalLine();

            // ── Tier 1 tunables ────────────────────────────────────────────
            Kino.UI.GroupLabel("Smoothing & snap (Tier 1)");

            Kino.UI.Label("Smoothing rate × (vanilla = 1.0)");
            float rate = s.smoothingRateMultiplier;
            Kino.UI.Slider(ref rate, 0.5f, 5.0f, "F2");
            if (!UnityEngine.Mathf.Approximately(rate, s.smoothingRateMultiplier))
            {
                s.smoothingRateMultiplier = rate;
                Settings.Save();
            }

            Kino.UI.Label("Snap threshold (m) — vanilla = 6m");
            float snap = s.snapThresholdMetres;
            Kino.UI.Slider(ref snap, 1.0f, 10.0f, "F1");
            if (!UnityEngine.Mathf.Approximately(snap, s.snapThresholdMetres))
            {
                s.snapThresholdMetres = snap;
                Settings.Save();
            }

            Kino.UI.Label("Sanity filter — reject above this m/s implied speed");
            float maxSpd = s.maxImpliedSpeedMps;
            Kino.UI.Slider(ref maxSpd, 40f, 200f, "F0");
            if (!UnityEngine.Mathf.Approximately(maxSpd, s.maxImpliedSpeedMps))
            {
                s.maxImpliedSpeedMps = maxSpd;
                Settings.Save();
            }

            Kino.UI.Label("Sanity rejections this session: " + s.rejectedFramesThisSession);

            Kino.UI.HorizontalLine();

            // ── Tier 2/3 placeholders ──────────────────────────────────────
            Kino.UI.GroupLabel("Tier 2 / 3 — pending implementation");

            bool t2a = s.tier2InputHistory;
            Kino.UI.Toggle("[stub] Tier 2: Input history broadcast", ref t2a);
            if (t2a != s.tier2InputHistory) { s.tier2InputHistory = t2a; Settings.Save(); }

            bool t2b = s.tier2UploadRateBoost;
            Kino.UI.Toggle("[stub] Tier 2: 50Hz upload-rate boost", ref t2b);
            if (t2b != s.tier2UploadRateBoost) { s.tier2UploadRateBoost = t2b; Settings.Save(); }

            bool t3 = s.tier3ContactArbitration;
            Kino.UI.Toggle("[stub] Tier 3: Contact arbitration", ref t3);
            if (t3 != s.tier3ContactArbitration) { s.tier3ContactArbitration = t3; Settings.Save(); }
        }
    }
}
