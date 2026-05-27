using KSL.API;
using NetcodeFix.Net;

namespace NetcodeFix
{
    // Draws inside KSL's mod-settings page. The Kino.UI.Slider "format"
    // parameter is actually the slider's *full label*, with the value
    // interpolated via $"… {value:F2}". Slider returns true when the value
    // changed this frame — cleaner than approximate-equals comparison.
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
            if (Kino.UI.Toggle("Enable NetcodeFix", ref en))
            {
                s.enabled = en;
                Settings.Save();
            }

            Kino.UI.HorizontalLine();

            // ── Peer presence ──────────────────────────────────────────────
            Kino.UI.GroupLabel("Modded peers in room: " + _presence.RemoteModdedCount);
            foreach (var p in _presence.Peers.Values)
                Kino.UI.Label("  • " + p.Name + "  v" + p.Version);
            Kino.UI.Label("Room capabilities: " + _presence.RoomCaps);

            Kino.UI.HorizontalLine();

            // ── Tier 1 tunables ────────────────────────────────────────────
            Kino.UI.GroupLabel("Smoothing & snap (Tier 1)");

            if (Kino.UI.Slider(ref s.smoothingRateMultiplier, 0.5f, 5.0f,
                    $"Smoothing rate × (vanilla = 1.0):  {s.smoothingRateMultiplier:F2}"))
                Settings.Save();

            if (Kino.UI.Slider(ref s.snapThresholdMetres, 1.0f, 10.0f,
                    $"Snap threshold (vanilla = 6m):  {s.snapThresholdMetres:F1} m"))
                Settings.Save();

            if (Kino.UI.Slider(ref s.maxImpliedSpeedMps, 40f, 200f,
                    $"Sanity reject above:  {s.maxImpliedSpeedMps:F0} m/s"))
                Settings.Save();

            Kino.UI.Label("Sanity rejections this session: " + s.rejectedFramesThisSession);

            Kino.UI.HorizontalLine();

            // ── Tier 2/3 placeholders ──────────────────────────────────────
            Kino.UI.GroupLabel("Tier 2 / 3 — pending implementation");

            bool t2a = s.tier2InputHistory;
            if (Kino.UI.Toggle("[stub] Tier 2: Input history broadcast", ref t2a))
            {
                s.tier2InputHistory = t2a; Settings.Save();
            }

            bool t2b = s.tier2UploadRateBoost;
            if (Kino.UI.Toggle("[stub] Tier 2: 50Hz upload-rate boost", ref t2b))
            {
                s.tier2UploadRateBoost = t2b; Settings.Save();
            }

            bool t3 = s.tier3ContactArbitration;
            if (Kino.UI.Toggle("[stub] Tier 3: Contact arbitration", ref t3))
            {
                s.tier3ContactArbitration = t3; Settings.Save();
            }
        }
    }
}
