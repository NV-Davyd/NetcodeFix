using KSL.API;

namespace NetcodeFix
{
    public static class SettingsUI
    {
        public static void Draw()
        {
            float correctionRate = Settings.CorrectionRate;
            if (Kino.UI.Slider(ref correctionRate, 0.5f, 6f, $"Correction rate: {Settings.CorrectionRate:F2}"))
            {
                Settings.CorrectionRate = correctionRate;
                Settings.Save();
            }
        }
    }
}
