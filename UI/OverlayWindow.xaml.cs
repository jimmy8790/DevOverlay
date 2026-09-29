using System.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;

namespace DevOverlay.UI;

public partial class OverlayWindow : Window
{
    private readonly OverlayPositioningService _positioningService;
    private readonly OverlayViewModel _viewModel;

    public OverlayWindow(OverlayViewModel viewModel, OverlayPositioningService positioningService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _positioningService = positioningService;
        DataContext = _viewModel;
        Loaded += (_, _) => _positioningService.Apply(this, _viewModel.Settings.Position);
    }
}
