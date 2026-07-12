using KSL.API;

namespace NetcodeFix
{
    public static class SettingsUI
    {
        private static bool _debugOpen;

        public static void Draw()
        {
            if (Kino.UI.Button(_debugOpen ? "Back" : "Debug"))
            {
                _debugOpen = !_debugOpen;
            }

            if (_debugOpen)
            {
                DrawDebug();
            }
            else
            {
                PresenceSync.DrawPeers();
            }
        }

        private static void DrawDebug()
        {
            bool drawSyncOverlay = Settings.DrawSyncOverlay;
            if (Kino.UI.Toggle("Draw sync correction boxes", ref drawSyncOverlay))
            {
                Settings.DrawSyncOverlay = drawSyncOverlay;
                Settings.Save();
            }

            Kino.UI.Label("Blue: synchronized target. Green: locally simulated remote car. Cyan: pending correction.");

            bool manualCorrectionTuning = Settings.ManualCorrectionTuning;
            if (Kino.UI.Toggle("Manual correction tuning", ref manualCorrectionTuning))
            {
                Settings.ManualCorrectionTuning = manualCorrectionTuning;
                Settings.Save();
            }

            if (Settings.ManualCorrectionTuning)
            {
                float correctionRate = Settings.CorrectionRate;
                if (Kino.UI.Slider(
                    ref correctionRate,
                    Settings.MinCorrectionRate,
                    Settings.MaxCorrectionRate,
                    $"Base correction rate: {Settings.CorrectionRate:F2}"))
                {
                    Settings.CorrectionRate = correctionRate;
                    Settings.Save();
                }

                Kino.UI.Label("Expert override. Higher values react faster but can add jitter and fight contact physics.");
            }
            else
            {
                Kino.UI.Label("Automatic correction is active. Base rate: 1.00.");
            }
        }
    }
}
