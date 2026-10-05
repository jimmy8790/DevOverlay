using System.Windows;
using DevOverlay.Configuration;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;
using DevOverlay.UI;
using Xunit;

namespace DevOverlay.Tests;

[Collection(WpfCollection.Name)]
public sealed class GlobalHotkeyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProductionStartupContinuesWhenDefaultRegistrationFailsAndPreservesVisibility(bool visible)
    {
        RunOnSta(() =>
        {
            var settings = OverlaySettings.CreateDefault() with { IsVisible = visible };
            var window = new OverlayWindow(new OverlayViewModel(settings,
                System.Windows.Threading.Dispatcher.CurrentDispatcher), new OverlayPositioningService());
            var native = new FakeHotkeyNative { RejectedVirtualKey = OverlayHotkey.DefaultVirtualKey };
            using var hotkeys = new GlobalHotkeyService(native);
            var initialized = false;
            window.SourceInitialized += (_, _) => initialized = true;
            try
            {
                var status = App.InitializeOverlay(window, hotkeys, settings);
                Assert.True(initialized);
                Assert.NotEqual(nint.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
                Assert.Equal(visible, window.IsVisible);
                Assert.Contains("이미 사용", status);
                Assert.Null(hotkeys.ActiveHotkey);
                window.Close();
                hotkeys.Dispose();
                hotkeys.Dispose();
                Assert.Empty(native.UnregisteredIds);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void MissingHandleIsRejectedAndRepeatedAttachDoesNotDuplicateRegistration()
    {
        RunOnSta(() =>
        {
            var native = new FakeHotkeyNative();
            using var service = new GlobalHotkeyService(native);
            Assert.False(service.TryReplace(OverlayHotkey.Default, out var error));
            Assert.Contains("준비", error);
            var window = new Window();
            try
            {
                Assert.True(service.Attach(window, OverlayHotkey.Default, out error), error);
                Assert.True(service.Attach(window, OverlayHotkey.Default, out error), error);
                Assert.Single(native.SuccessfulRegistrations);
                window.Close();
                service.Dispose();
                Assert.Single(native.UnregisteredIds);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void RealUser32ExportsRegisterAndUnregisterSuccessfully()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            using var service = new GlobalHotkeyService();
            try
            {
                var key = new OverlayHotkey(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift |
                    OverlayHotkeyModifiers.Alt, 0x7A);
                Assert.True(service.Attach(window, key, out var error), error);
                service.Dispose();
                using var second = new GlobalHotkeyService();
                Assert.True(second.Attach(window, key, out error), error);
                second.Dispose();
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DefaultsAndPersistedSemanticPartsRoundTripAndInvalidValuesFallBackSafely()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DevOverlayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var store = new OverlaySettingsStore(path);
        var hotkey = new OverlayHotkey(OverlayHotkeyModifiers.Alt | OverlayHotkeyModifiers.Shift, 0x70);
        try
        {
            store.Save(OverlaySettings.CreateDefault() with { Hotkey = hotkey });
            Assert.Equal(hotkey, store.Load().Hotkey);

            File.WriteAllText(path, """{"HotkeyModifiers":255,"HotkeyVirtualKey":0}""");
            Assert.Equal(OverlayHotkey.Default, store.Load().Hotkey);
            File.WriteAllText(path, "{}");
            Assert.Equal(OverlayHotkey.Default, store.Load().Hotkey);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RegistrationConflictKeepsTheExistingHotkeyAndReportsTheConflict()
    {
        RunOnSta(() =>
        {
            var native = new FakeHotkeyNative { RejectedVirtualKey = 0x70 };
            var window = new Window { ShowActivated = false };
            using var service = new GlobalHotkeyService(native);
            try
            {
                Assert.True(service.Attach(window, OverlayHotkey.Default, out var initialError), initialError);
                var requested = new OverlayHotkey(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, 0x70);

                Assert.False(service.TryReplace(requested, out var error));
                Assert.Equal(OverlayHotkey.Default, service.ActiveHotkey);
                Assert.Contains("이미 사용", error);
                Assert.Single(native.SuccessfulRegistrations);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void InvalidRequestedBindingIsRejectedWithoutChangingTheExistingHotkey()
    {
        RunOnSta(() =>
        {
            var native = new FakeHotkeyNative();
            var window = new Window { ShowActivated = false };
            using var service = new GlobalHotkeyService(native);
            try
            {
                Assert.True(service.Attach(window, OverlayHotkey.Default, out var initialError), initialError);
                Assert.False(service.TryReplace(new OverlayHotkey(OverlayHotkeyModifiers.None, 0x41), out var error));
                Assert.Equal(OverlayHotkey.Default, service.ActiveHotkey);
                Assert.Contains("modifier", error);
                Assert.Single(native.SuccessfulRegistrations);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SuccessfulReplacementRegistersCandidateBeforeReleasingOldBindingAndDisposesOnce()
    {
        RunOnSta(() =>
        {
            var native = new FakeHotkeyNative();
            var window = new Window { ShowActivated = false };
            var service = new GlobalHotkeyService(native);
            try
            {
                Assert.True(service.Attach(window, OverlayHotkey.Default, out var initialError), initialError);
                var requested = new OverlayHotkey(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, 0x71);

                Assert.True(service.TryReplace(requested, out var error), error);
                Assert.Equal(requested, service.ActiveHotkey);
                Assert.Equal(2, native.SuccessfulRegistrations.Count);
                Assert.Single(native.UnregisteredIds);
                Assert.NotEqual(native.SuccessfulRegistrations[0].Id, native.SuccessfulRegistrations[1].Id);

                service.Dispose();
                service.Dispose();
                Assert.Equal(2, native.UnregisteredIds.Count);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WmHotkeyDispatchesOnlyTheRegisteredApplicationIdsAndIsSuppressedDuringCapture()
    {
        RunOnSta(() =>
        {
            var native = new FakeHotkeyNative();
            var window = new Window { ShowActivated = false };
            using var service = new GlobalHotkeyService(native);
            var presses = 0;
            service.Pressed += () => presses++;
            try
            {
                Assert.True(service.Attach(window, OverlayHotkey.Default, out var error), error);
                Assert.False(service.ProcessWindowMessage(0x0312, (nint)1));
                Assert.True(service.ProcessWindowMessage(0x0312, (nint)0x444F));
                service.SetCaptureActive(true);
                Assert.False(service.ProcessWindowMessage(0x0312, (nint)0x444F));
                Assert.Equal(1, presses);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void HotkeyViewModelOnlyCommitsAfterTheHostAcceptsTheRequestedBinding()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        var requested = new OverlayHotkey(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, 0x71);
        viewModel.HotkeyChangeRequested += hotkey => viewModel.UpdateHotkey(hotkey, "Global hotkey: active");

        viewModel.RequestHotkeyChange(requested);

        Assert.Contains("Ctrl + Alt + F2", viewModel.HotkeyText);
        Assert.Equal("Global hotkey: active", viewModel.HotkeyStatusText);
    }

    [Fact]
    public void RestoringHiddenOverlayUsesTheExistingWindowInstance()
    {
        RunOnSta(() =>
        {
            var overlay = new OverlayWindow(new OverlayViewModel(OverlaySettings.CreateDefault(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher), new OverlayPositioningService());
            try
            {
                overlay.Show();
                overlay.Hide();
                overlay.ShowWithoutActivation();
                Assert.True(overlay.IsVisible);
                Assert.True(overlay.Topmost);
            }
            finally { overlay.Close(); }
        });
    }

    private sealed class FakeHotkeyNative : IGlobalHotkeyNative
    {
        public uint? RejectedVirtualKey { get; init; }
        public int LastError { get; private set; }
        public List<(int Id, uint VirtualKey)> SuccessfulRegistrations { get; } = [];
        public List<int> UnregisteredIds { get; } = [];

        public bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey)
        {
            if (virtualKey == RejectedVirtualKey)
            {
                LastError = 1409;
                return false;
            }
            SuccessfulRegistrations.Add((id, virtualKey));
            LastError = 0;
            return true;
        }

        public bool UnregisterHotKey(nint windowHandle, int id)
        {
            UnregisteredIds.Add(id);
            LastError = 0;
            return true;
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
