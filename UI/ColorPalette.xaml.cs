using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevOverlay.Configuration;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace DevOverlay.UI;

/// <summary>
/// Compact HSV picker plus common swatches. It only reports "#RRGGBB" strings; the owner decides
/// what setting they change, so hex entry and the palette stay two views of one stored value.
/// </summary>
public partial class ColorPalette : UserControl
{
    private const double SvWidth = 200;
    private const double SvHeight = 130;
    private const double HueWidth = 200;
    // Live apply persists settings; limit drag updates so the palette does not write the file at mouse-move rate.
    private static readonly long EmitIntervalTicks = Stopwatch.Frequency / 20;
    private static readonly string[] Presets =
    [
        "#FFFFFF", "#F5F7FA", "#AFB9C8", "#808080", "#3A4048", "#1A1D23", "#000000", "#FF5252",
        "#FFA726", "#FFEB3B", "#66BB6A", "#26C6DA", "#42A5F5", "#5C6BC0", "#AB47BC", "#EC407A"
    ];
    private double _hue;
    private double _saturation;
    private double _value = 1;
    private long _lastEmit;
    private bool _pendingEmit;

    public ColorPalette()
    {
        InitializeComponent();
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var step = 0; step <= 6; step++)
        {
            var rgb = OverlayColor.FromHsv(step * 60, 1, 1);
            rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(rgb.Red, rgb.Green, rgb.Blue), step / 6d));
        }
        HueBar.Background = rainbow;
        foreach (var preset in Presets)
        {
            var swatch = new Button
            {
                Width = 20, Height = 20, Margin = new Thickness(2), Padding = new Thickness(0), Tag = preset,
                ToolTip = preset, Background = ToBrush(OverlayColor.Parse(preset))
            };
            swatch.Click += OnPresetClick;
            PresetPanel.Children.Add(swatch);
        }
        UpdateVisuals();
    }

    /// <summary>Raised with a normalized "#RRGGBB" value when the user picks a color.</summary>
    public event Action<string>? ColorPicked;

    /// <summary>Loads a color without raising <see cref="ColorPicked"/>.</summary>
    public void SetColor(string hex)
    {
        if (!OverlayColor.TryNormalize(hex, out var normalized)) return;
        var (hue, saturation, value) = OverlayColor.ToHsv(OverlayColor.Parse(normalized));
        // Grays have no meaningful hue; keep the previous one so the hue bar does not jump.
        if (saturation > 0 && value > 0) _hue = hue;
        _saturation = saturation;
        _value = value;
        UpdateVisuals();
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        var hex = (string)((FrameworkElement)sender).Tag;
        SetColor(hex);
        ColorPicked?.Invoke(hex);
    }

    private void OnSvMouseDown(object sender, MouseButtonEventArgs e)
    {
        SvArea.CaptureMouse();
        UpdateFromSv(e.GetPosition(SvArea));
    }

    private void OnSvMouseMove(object sender, MouseEventArgs e)
    {
        if (SvArea.IsMouseCaptured) UpdateFromSv(e.GetPosition(SvArea));
    }

    private void OnSvMouseUp(object sender, MouseButtonEventArgs e) => Release(SvArea);

    private void OnHueMouseDown(object sender, MouseButtonEventArgs e)
    {
        HueBar.CaptureMouse();
        UpdateFromHue(e.GetPosition(HueBar));
    }

    private void OnHueMouseMove(object sender, MouseEventArgs e)
    {
        if (HueBar.IsMouseCaptured) UpdateFromHue(e.GetPosition(HueBar));
    }

    private void OnHueMouseUp(object sender, MouseButtonEventArgs e) => Release(HueBar);

    private void Release(UIElement element)
    {
        element.ReleaseMouseCapture();
        if (_pendingEmit) Emit(force: true);
    }

    private void UpdateFromSv(Point point)
    {
        _saturation = Math.Clamp(point.X / SvWidth, 0, 1);
        _value = 1 - Math.Clamp(point.Y / SvHeight, 0, 1);
        UpdateVisuals();
        Emit(force: false);
    }

    private void UpdateFromHue(Point point)
    {
        _hue = Math.Clamp(point.X / HueWidth, 0, 1) * 359.999;
        UpdateVisuals();
        Emit(force: false);
    }

    private void Emit(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now - _lastEmit < EmitIntervalTicks)
        {
            _pendingEmit = true;
            return;
        }
        _lastEmit = now;
        _pendingEmit = false;
        ColorPicked?.Invoke(OverlayColor.ToHex(OverlayColor.FromHsv(_hue, _saturation, _value)));
    }

    private void UpdateVisuals()
    {
        var pure = OverlayColor.FromHsv(_hue, 1, 1);
        SvArea.Background = ToBrush(pure);
        Canvas.SetLeft(SvThumb, _saturation * SvWidth - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, (1 - _value) * SvHeight - SvThumb.Height / 2);
        Canvas.SetLeft(HueThumb, _hue / 360 * HueWidth - HueThumb.Width / 2);
    }

    private static Brush ToBrush(RgbColor color) => new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
}
