using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DevOverlay.Configuration;

namespace DevOverlay.Platform.Windows;

/// <summary>Registers one application-owned visibility hotkey without using keyboard hooks.</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int PrimaryRegistrationId = 0x444F;
    private const int AlternateRegistrationId = 0x4450;
    private readonly IGlobalHotkeyNative _native;
    private HwndSource? _source;
    private nint _windowHandle;
    private int? _activeRegistrationId;
    private OverlayHotkey? _activeHotkey;
    private bool _isCapturing;
    private bool _disposed;

    public GlobalHotkeyService(IGlobalHotkeyNative? native = null) => _native = native ?? new GlobalHotkeyNative();

    public event Action? Pressed;

    public OverlayHotkey? ActiveHotkey => _activeHotkey;

    /// <summary>Creates the overlay's real HWND while it is hidden when necessary, then hooks its message loop.</summary>
    public bool Attach(Window overlayWindow, OverlayHotkey hotkey, out string? error)
    {
        ArgumentNullException.ThrowIfNull(overlayWindow);
        ThrowIfDisposed();

        if (_source is not null) return TryReplace(hotkey.Normalize(), out error);
        try
        {
            // EnsureHandle synchronously raises SourceInitialized; registration follows that lifecycle point.
            _windowHandle = new WindowInteropHelper(overlayWindow).EnsureHandle();
            _source = HwndSource.FromHwnd(_windowHandle);
            if (_windowHandle == nint.Zero || _source is null || _source.IsDisposed)
            {
                error = "HUD 창의 HWND가 준비되지 않았습니다.";
                _source = null;
                return false;
            }
            _source.AddHook(WindowProcedure);
            return TryRegisterInitial(hotkey, out error);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or
            EntryPointNotFoundException or DllNotFoundException)
        {
            error = $"단축키 초기화 실패: {exception.Message}";
            return false;
        }
    }

    /// <summary>Suppresses the registered action while the Settings capture field owns keyboard input.</summary>
    public void SetCaptureActive(bool active) => _isCapturing = active;

    /// <summary>Registers the candidate first; the working binding remains registered if that fails.</summary>
    public bool TryReplace(OverlayHotkey requested, out string? error)
    {
        ThrowIfDisposed();
        if (!requested.IsValid)
        {
            error = "유효한 modifier + key 조합을 입력하세요.";
            return false;
        }

        if (_activeHotkey == requested)
        {
            error = null;
            return true;
        }

        if (_activeRegistrationId is null)
        {
            return TryRegisterInitial(requested, out error);
        }

        var candidateId = _activeRegistrationId == PrimaryRegistrationId ? AlternateRegistrationId : PrimaryRegistrationId;
        if (!TryRegister(candidateId, requested, out error)) return false;

        if (_native.UnregisterHotKey(_windowHandle, _activeRegistrationId.Value))
        {
            _activeRegistrationId = candidateId;
            _activeHotkey = requested;
            error = null;
            return true;
        }

        // Preserve the old registration if Windows refuses to release it; candidate cleanup is best effort.
        _native.UnregisterHotKey(_windowHandle, candidateId);
        error = "기존 단축키를 해제하지 못해 변경하지 않았습니다.";
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_activeRegistrationId is { } id) _native.UnregisterHotKey(_windowHandle, id);
        _activeRegistrationId = null;
        _activeHotkey = null;
        if (_source is { IsDisposed: false }) _source.RemoveHook(WindowProcedure);
        _source = null;
    }

    private bool TryRegisterInitial(OverlayHotkey hotkey, out string? error)
    {
        hotkey = hotkey.Normalize();
        if (!TryRegister(PrimaryRegistrationId, hotkey, out error)) return false;
        _activeRegistrationId = PrimaryRegistrationId;
        _activeHotkey = hotkey;
        return true;
    }

    private bool TryRegister(int id, OverlayHotkey hotkey, out string? error)
    {
        if (_windowHandle == nint.Zero)
        {
            error = "HUD 창이 아직 준비되지 않았습니다.";
            return false;
        }

        if (_native.RegisterHotKey(_windowHandle, id, ToNativeModifiers(hotkey.Modifiers), hotkey.VirtualKey))
        {
            error = null;
            return true;
        }

        var win32Error = _native.LastError;
        error = win32Error == 1409
            ? "이 단축키는 다른 프로그램에서 이미 사용 중입니다. 기존 단축키를 유지합니다."
            : $"단축키 등록에 실패했습니다. (Windows error {win32Error})";
        return false;
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        handled = ProcessWindowMessage(message, wParam);
        return nint.Zero;
    }

    internal bool ProcessWindowMessage(int message, nint wParam)
    {
        if (_disposed || message != WmHotkey || _isCapturing || _activeRegistrationId is not { } activeId ||
            wParam.ToInt64() != activeId) return false;
        Pressed?.Invoke();
        return true;
    }

    private static uint ToNativeModifiers(OverlayHotkeyModifiers modifiers)
    {
        const uint noRepeat = 0x4000;
        var native = 0u;
        if (modifiers.HasFlag(OverlayHotkeyModifiers.Alt)) native |= 0x0001;
        if (modifiers.HasFlag(OverlayHotkeyModifiers.Control)) native |= 0x0002;
        if (modifiers.HasFlag(OverlayHotkeyModifiers.Shift)) native |= 0x0004;
        if (modifiers.HasFlag(OverlayHotkeyModifiers.Windows)) native |= 0x0008;
        return native | noRepeat;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GlobalHotkeyService));
    }
}

public interface IGlobalHotkeyNative
{
    bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);
    bool UnregisterHotKey(nint windowHandle, int id);
    int LastError { get; }
}

internal sealed class GlobalHotkeyNative : IGlobalHotkeyNative
{
    public int LastError { get; private set; }

    public bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey)
    {
        var success = RegisterHotKeyNative(windowHandle, id, modifiers, virtualKey);
        LastError = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }

    public bool UnregisterHotKey(nint windowHandle, int id)
    {
        var success = UnregisterHotKeyNative(windowHandle, id);
        LastError = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", ExactSpelling = true, SetLastError = true)]
    private static extern bool RegisterHotKeyNative(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", ExactSpelling = true, SetLastError = true)]
    private static extern bool UnregisterHotKeyNative(nint hWnd, int id);
}
