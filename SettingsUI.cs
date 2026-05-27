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
        private readonly PeerPresence   _presence;
        private readonly HighRateSync   _highRateSync;
        private readonly ContactArbiter _contactArbiter;

        public SettingsUI(PeerPresence presence, HighRateSync highRateSync, ContactArbiter contactArbiter)
        {
            _presence       = presence;
            _highRateSync   = highRateSync;
            _contactArbiter = contactArbiter;
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

            // ── Tier 2: supplemental high-rate sync ────────────────────────
            Kino.UI.GroupLabel("Tier 2 — modded-room only (auto-disables in vanilla rooms)");

            bool t2a = s.tier2InputHistory;
            if (Kino.UI.Toggle("High-rate pose broadcast (50Hz via Kino.Sync)", ref t2a))
            {
                s.tier2InputHistory = t2a; Settings.Save();
            }
            Kino.UI.Label("  Sent: " + _highRateSync.FramesSent
                       + "    Injected (received from peers): " + _highRateSync.FramesInject
                       + "    Dropped: " + _highRateSync.FramesDrop);

            bool t2b = s.tier2UploadRateBoost;
            if (Kino.UI.Toggle("Boost local upload rate to 50Hz (vanilla 30Hz)", ref t2b))
            {
                s.tier2UploadRateBoost = t2b; Settings.Save();
            }

            Kino.UI.HorizontalLine();

            // ── Tier 3: contact arbitration ────────────────────────────────
            Kino.UI.GroupLabel("Tier 3 — modded-room only");

            bool t3 = s.tier3ContactArbitration;
            if (Kino.UI.Toggle("Contact arbitration (deterministic impulse on both sides)", ref t3))
            {
                s.tier3ContactArbitration = t3; Settings.Save();
            }

            Kino.UI.Label("  Detected: " + _contactArbiter.ContactsDetected
                       + "    Confirmed: " + _contactArbiter.ContactsConfirmed
                       + "    Phantom: " + _contactArbiter.ContactsPhantom
                       + "    Timed-out: " + _contactArbiter.ContactsTimedOut);
            Kino.UI.Label("  When active, vanilla collision physics is suppressed for");
            Kino.UI.Label("  modded peers — all impulses come from arbitration.");
        }
    }
}
