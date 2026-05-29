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
            float correctionRate = Settings.CorrectionRate;
            if (Kino.UI.Slider(
                ref correctionRate,
                Settings.MinCorrectionRate,
                Settings.MaxCorrectionRate,
                $"Correction rate: {Settings.CorrectionRate:F2}"))
            {
                Settings.CorrectionRate = correctionRate;
                Settings.Save();
            }

            Kino.UI.Label("Higher correction reduces visible offset but can make remote cars look more jittery.");
            Kino.UI.Label("Lower correction looks smoother, but remote cars can stay farther from their latest synced position.");
            Kino.UI.Label("If the room has high or unstable ping, use less correction. Stock CarX is close to 1.00.");
        }
    }
}
