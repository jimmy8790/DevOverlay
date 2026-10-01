namespace DevOverlay.Configuration;

/// <summary>
/// JSON-friendly HUD appearance (colors, background opacity and horizontal density); this type has no WPF dependencies.
/// <see cref="Spacing"/> controls only horizontal gaps. HUD font and vertical sizing are product defaults.
/// </summary>
public sealed record OverlayAppearance(
    string BackgroundColor,
    double BackgroundOpacity,
    string ValueTextColor,
    string LabelTextColor,
    double Spacing = OverlayAppearance.DefaultSpacing)
{
    // The fixed layout previously felt too loose, so the default is slightly tighter.
    public const double DefaultSpacing = 0.8;
    public const double MinSpacing = 0.4;
    public const double MaxSpacing = 1.6;

    public static OverlayAppearance CreateDefault() => new("#1A1D23", 227d / 255d, "#F5F7FA", "#AFB9C8");

    public OverlayAppearance Normalize()
    {
        var defaults = CreateDefault();
        return new OverlayAppearance(
            OverlayColor.TryNormalize(BackgroundColor, out var background) ? background : defaults.BackgroundColor,
            double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, 0, 1) : defaults.BackgroundOpacity,
            OverlayColor.TryNormalize(ValueTextColor, out var value) ? value : defaults.ValueTextColor,
            OverlayColor.TryNormalize(LabelTextColor, out var label) ? label : defaults.LabelTextColor,
            double.IsFinite(Spacing) ? Math.Clamp(Spacing, MinSpacing, MaxSpacing) : defaults.Spacing);
    }
}

public readonly record struct RgbColor(byte Red, byte Green, byte Blue);

/// <summary>Keeps color serialization and validation outside the WPF presentation layer.</summary>
public static class OverlayColor
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.Length == 7 && text[0] == '#') text = text[1..];
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb)) return false;
        normalized = $"#{rgb:X6}";
        return true;
    }

    public static RgbColor Parse(string normalized)
    {
        if (!TryNormalize(normalized, out var value)) throw new ArgumentException("A six-digit RGB color is required.", nameof(normalized));
        var rgb = Convert.ToUInt32(value[1..], 16);
        return new RgbColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    public static string ToHex(RgbColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    /// <summary>Hue is degrees in [0, 360); saturation and value are in [0, 1].</summary>
    public static (double Hue, double Saturation, double Value) ToHsv(RgbColor color)
    {
        double r = color.Red / 255d, g = color.Green / 255d, b = color.Blue / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        double hue;
        if (delta == 0) hue = 0;
        else if (max == r) hue = 60 * (((g - b) / delta) % 6);
        else if (max == g) hue = 60 * (((b - r) / delta) + 2);
        else hue = 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    public static RgbColor FromHsv(double hue, double saturation, double value)
    {
        hue = double.IsFinite(hue) ? ((hue % 360) + 360) % 360 : 0;
        saturation = double.IsFinite(saturation) ? Math.Clamp(saturation, 0, 1) : 0;
        value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        var m = value - chroma;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0d),
            1 => (x, chroma, 0d),
            2 => (0d, chroma, x),
            3 => (0d, x, chroma),
            4 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return new RgbColor(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    private static byte ToByte(double channel) => (byte)Math.Clamp((int)Math.Round(channel * 255), 0, 255);
}
