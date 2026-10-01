namespace DevOverlay.Metrics.Windows;

/// <summary>Converts protocol-reported consumed quota into the HUD's remaining-quota meaning.</summary>
internal static class QuotaPercentage
{
    public static int ToRemaining(int usedPercent) => 100 - Math.Clamp(usedPercent, 0, 100);
}
