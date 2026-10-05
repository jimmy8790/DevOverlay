using System.Windows;
using System.Windows.Input;
using DevOverlay.Configuration;
using DevOverlay.Presentation;
using DevOverlay.Platform.Windows;

namespace DevOverlay.UI;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        System.Windows.DataObject.AddPastingHandler(RefreshIntervalTextBox, OnRefreshIntervalPasting);
    }

    private Action<string>? _colorSink;

    private void OnPickColorClick(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        var viewModel = (SettingsViewModel)DataContext;
        var (current, sink) = (string)button.Tag switch
        {
            "Background" => (viewModel.BackgroundColor, (Action<string>)(hex => viewModel.BackgroundColor = hex)),
            "Text" => (viewModel.ValueTextColor, (Action<string>)(hex => viewModel.ValueTextColor = hex)),
            _ => (viewModel.LabelTextColor, (Action<string>)(hex => viewModel.LabelTextColor = hex))
        };
        if (_colorSink is null) Palette.ColorPicked += OnPaletteColorPicked;
        _colorSink = sink;
        Palette.SetColor(current);
        ColorPopup.PlacementTarget = button;
        ColorPopup.IsOpen = true;
    }

    // The sink writes the same view-model property as the hex box, which keeps swatch, hex and overlay in sync.
    private void OnPaletteColorPicked(string hex) => _colorSink?.Invoke(hex);

    private void OnHexKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        System.Windows.Data.BindingOperations
            .GetBindingExpression((System.Windows.Controls.TextBox)sender, System.Windows.Controls.TextBox.TextProperty)
            ?.UpdateSource();
    }

    private void OnRefreshIntervalLostFocus(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).CommitRefreshIntervalText();

    private void OnRefreshIntervalKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        ((SettingsViewModel)DataContext).CommitRefreshIntervalText();
        e.Handled = true;
    }

    private void OnRefreshIntervalPreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsAsciiDigit(character));

    private static void OnRefreshIntervalPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(typeof(string)))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(typeof(string)) as string;
        if (string.IsNullOrEmpty(text) || text.Any(character => !char.IsAsciiDigit(character))) e.CancelCommand();
    }

    private void OnHotkeyCaptureGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ((SettingsViewModel)DataContext).SetHotkeyCaptureActive(true);

    private void OnHotkeyCaptureLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ((SettingsViewModel)DataContext).SetHotkeyCaptureActive(false);

    private void OnHotkeyCapturePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var viewModel = (SettingsViewModel)DataContext;
        if (e.Key == Key.Escape)
        {
            viewModel.UpdateHotkeyStatus("단축키 변경을 취소했습니다.");
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            viewModel.UpdateHotkeyStatus("modifier와 다른 키를 함께 누르세요.");
            e.Handled = true;
            return;
        }

        var modifiers = ToModifiers(Keyboard.Modifiers);
        var hotkey = new OverlayHotkey(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (!hotkey.IsValid)
        {
            viewModel.UpdateHotkeyStatus("유효한 modifier + key 조합을 입력하세요.");
            e.Handled = true;
            return;
        }

        viewModel.RequestHotkeyChange(hotkey);
        e.Handled = true;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static OverlayHotkeyModifiers ToModifiers(ModifierKeys modifiers)
    {
        var result = OverlayHotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= OverlayHotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= OverlayHotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= OverlayHotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= OverlayHotkeyModifiers.Windows;
        return result;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMoveOverlayClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).RequestMoveOverlay();

    private void OnResetPositionClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).ResetPosition();

    private void OnMoveGroupUpClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).MoveGroup(((GroupOrderChoice)((FrameworkElement)sender).DataContext).Category, -1);

    private void OnMoveGroupDownClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).MoveGroup(((GroupOrderChoice)((FrameworkElement)sender).DataContext).Category, 1);

    private void OnResetAppearanceClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).ResetAppearance();

    private void OnInstallPawnIoClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).RequestPawnIoInstall();

    private void OnInstallSensorServiceClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).RequestSensorServiceAction(SensorServiceAction.Install);

    private void OnInstallPresentMonClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).RequestPresentMonInstall();

    private void OnRecheckCodexClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).RequestCodexRecheck();

    private void OnCopyCommandClick(object sender, RoutedEventArgs e)
    {
        var command = ((FrameworkElement)sender).Tag as string;
        if (string.IsNullOrWhiteSpace(command)) return;

        try
        {
            System.Windows.Clipboard.SetText(command);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            System.Windows.MessageBox.Show(this, "The command could not be copied. Try again after closing another clipboard-using application.", "Copy command", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRecheckClaudeClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).RequestClaudeRecheck();

    private void OnBrowseFpsTargetClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Applications (*.exe)|*.exe",
            CheckFileExists = true,
            Title = "Choose FPS application"
        };
        if (picker.ShowDialog(this) == true)
            ((SettingsViewModel)DataContext).FpsTargetId = picker.FileName;
    }

    private void OnRepairSensorServiceClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).RequestSensorServiceAction(SensorServiceAction.Repair);

    private void OnUninstallSensorServiceClick(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).RequestSensorServiceAction(SensorServiceAction.Uninstall);

    private void OnForgetPeripheralClick(object sender, RoutedEventArgs e)
    {
        var row = (PeripheralDeviceViewModel)((FrameworkElement)sender).DataContext;
        var answer = System.Windows.MessageBox.Show(this,
            $"Remove \"{row.FriendlyName}\" from DevOverlay?\n\nThis deletes only DevOverlay's saved settings for it (HUD name and visibility). " +
            "Windows, drivers, Bluetooth pairing and the device itself are not changed. If the device is detected again, " +
            "it will be listed again with default settings.",
            "Forget device", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) ((SettingsViewModel)DataContext).ForgetPeripheral(row.Identity);
    }

    private void OnViewReleaseClick(object sender, RoutedEventArgs e) => ((SettingsViewModel)DataContext).RequestOpenLatestRelease();
    private void OnCheckForUpdatesClick(object sender, RoutedEventArgs e) => ((SettingsViewModel)DataContext).RequestUpdateCheck();
    private void OnOpenGitHubClick(object sender, RoutedEventArgs e) => ((SettingsViewModel)DataContext).RequestOpenRepository();
    private void OnViewReleasesClick(object sender, RoutedEventArgs e) => ((SettingsViewModel)DataContext).RequestOpenReleases();

    private async void OnExportPeripheralDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var factory = ((SettingsViewModel)DataContext).PeripheralDiagnosticsFactory;
        if (factory is null) return;
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Peripheral Diagnostics", Filter = "JSON diagnostics (*.json)|*.json",
            FileName = "DevOverlay-peripherals.json", DefaultExt = ".json", AddExtension = true
        };
        if (picker.ShowDialog(this) != true) return;
        var button = (System.Windows.Controls.Button)sender;
        button.IsEnabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var json = await factory(timeout.Token);
            await System.IO.File.WriteAllTextAsync(picker.FileName, json, timeout.Token);
            System.Windows.MessageBox.Show(this, "Diagnostics exported. Review product names before sharing the file.", "Peripheral diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Write($"[Peripheral] Export failed Type={exception.GetType().Name}");
            System.Windows.MessageBox.Show(this, "Diagnostics could not be exported. Check the destination and try again.", "Peripheral diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = true; }
    }
}
