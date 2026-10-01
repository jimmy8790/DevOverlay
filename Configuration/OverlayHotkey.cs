namespace DevOverlay.Configuration;

/// <summary>Persisted, layout-independent representation of the HUD visibility hotkey.</summary>
[Flags]
public enum OverlayHotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public readonly record struct OverlayHotkey(OverlayHotkeyModifiers Modifiers, uint VirtualKey)
{
    public const uint DefaultVirtualKey = 0x4F; // O
    private const OverlayHotkeyModifiers AllowedModifiers =
        OverlayHotkeyModifiers.Alt | OverlayHotkeyModifiers.Control |
        OverlayHotkeyModifiers.Shift | OverlayHotkeyModifiers.Windows;

    public static OverlayHotkey Default { get; } =
        new(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift, DefaultVirtualKey);

    public bool IsValid => Modifiers != OverlayHotkeyModifiers.None &&
                           (Modifiers & ~AllowedModifiers) == 0 &&
                           VirtualKey is > 0 and not 0x10 and not 0x11 and not 0x12 and not 0x5B and not 0x5C;

    public OverlayHotkey Normalize() => IsValid ? this : Default;
}
