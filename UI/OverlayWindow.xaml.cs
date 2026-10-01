using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DevOverlay.Configuration;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;

namespace DevOverlay.UI;

public partial class OverlayWindow : Window
{
    private readonly OverlayPositioningService _positioningService;
    private OverlayPosition _position;
    private OverlayAppearance _appearance;
    private bool _hasRendered;
    private bool _isPositionEditing;

    public OverlayWindow(OverlayViewModel viewModel, OverlayPositioningService positioningService)
    {
        InitializeComponent();
        _position = viewModel.Settings.Position;
        _appearance = viewModel.Settings.Appearance;
        _positioningService = positioningService;
        DataContext = viewModel;
        ContentRendered += OnContentRendered;
        SizeChanged += OnSizeChanged;
        Closed += OnClosed;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        ApplyAppearance(_appearance);
    }

    public event Action<OverlayPosition>? CustomPositionChanged;

    public void ApplySettings(OverlayPosition position, OverlayAppearance appearance)
    {
        if (_appearance != appearance)
        {
            _appearance = appearance;
            ApplyAppearance(appearance);
        }
        _position = position;
        if (_hasRendered) ApplyPosition();
    }

    public void ReapplyPosition() => ApplyPosition();

    /// <summary>Restores the existing HUD instance without taking focus from the foreground application.</summary>
    public void ShowWithoutActivation()
    {
        if (!IsVisible) Show(); // ShowActivated=false is declared on the WPF window.
        Topmost = true;
        Dispatcher.BeginInvoke(ReapplyPosition, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void EnablePositionEditing()
    {
        _isPositionEditing = true;
        Cursor = System.Windows.Input.Cursors.SizeAll;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        _hasRendered = true;
        ApplyPosition();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hasRendered && !_isPositionEditing && (e.WidthChanged || e.HeightChanged))
        {
            ApplyPosition();
        }
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_hasRendered && (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SystemParameters.WorkArea)))
        {
            // Wait for WPF to publish the replacement work area after a DPI, taskbar, or display change.
            Dispatcher.BeginInvoke(ApplyPosition);
        }
    }

    private void ApplyPosition() => _positioningService.TryApply(this, _position);

    private void ApplyAppearance(OverlayAppearance appearance)
    {
        var normalized = appearance.Normalize();
        SetBrush("OverlayBackgroundBrush", normalized.BackgroundColor, normalized.BackgroundOpacity);
        if (Resources["OverlayBorderBrush"] is SolidColorBrush border)
        {
            // Keep the brush's configured alpha; opacity is only a render multiplier.
            if (border.IsFrozen)
            {
                border = border.CloneCurrentValue();
            }
            border.Opacity = normalized.BackgroundOpacity;
            Resources["OverlayBorderBrush"] = border;
        }
        SetBrush("OverlayValueBrush", normalized.ValueTextColor, 1);
        SetBrush("OverlayLabelBrush", normalized.LabelTextColor, 1);
        ApplyLayout(normalized);
    }

    /// <summary>Runs only when the appearance record changes; telemetry updates never re-enter this path.</summary>
    private void ApplyLayout(OverlayAppearance appearance)
    {
        var layout = OverlayLayoutMetrics.Create(appearance);
        Resources["OverlayValueFontSize"] = layout.ValueFontSize;
        Resources["OverlayLabelFontSize"] = layout.LabelFontSize;
        Resources["OverlaySeparatorHeight"] = layout.SeparatorHeight;
        Resources["OverlayBorderPadding"] = new Thickness(
            layout.BorderPaddingX, layout.BorderPaddingY, layout.BorderPaddingX, layout.BorderPaddingY);
        Resources["OverlayCornerRadius"] = new CornerRadius(layout.CornerRadius);
        Resources["OverlayLabelMargin"] = new Thickness(0, 0, layout.LabelGap, 0);
        Resources["OverlayPrefixMargin"] = new Thickness(0, 0, layout.PrefixValueGap, 0);
        Resources["OverlayMetricMargin"] = new Thickness(0, 0, layout.MetricGap, 0);
        Resources["OverlayGroupMargin"] = new Thickness(
            layout.GroupPaddingLeft, layout.GroupPaddingY, layout.GroupPaddingRight, layout.GroupPaddingY);
        Resources["OverlaySeparatorMargin"] = new Thickness(
            layout.SeparatorMarginLeft, 0, layout.SeparatorMarginRight, 0);
        RuntimeDiagnostics.Write($"[Spacing] ApplyLayout=true Percent={appearance.Spacing * 100:0} LabelGap={layout.LabelGap} PrefixGap={layout.PrefixValueGap} MetricGap={layout.MetricGap} GroupMargin={Resources["OverlayGroupMargin"]} SeparatorMargin={Resources["OverlaySeparatorMargin"]}");
    }

    private void SetBrush(string resourceKey, string value, double opacity)
    {
        if (Resources[resourceKey] is SolidColorBrush brush)
        {
            if (brush.IsFrozen)
            {
                brush = brush.CloneCurrentValue();
            }
            var color = OverlayColor.Parse(value);
            brush.Color = System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue);
            brush.Opacity = opacity;
            // Styles may freeze the brush as soon as it enters their resource scope.
            // Finish all edits before replacing the live resource.
            Resources[resourceKey] = brush;
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isPositionEditing)
        {
            return;
        }

        try
        {
            DragMove();
            _position = _positioningService.CaptureCustomPosition(this);
            ApplyPosition();
            CustomPositionChanged?.Invoke(_position);
        }
        catch (InvalidOperationException)
        {
            // DragMove can be cancelled when the mouse sequence is interrupted.
        }
        finally
        {
            _isPositionEditing = false;
            Cursor = System.Windows.Input.Cursors.Arrow;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }
}
