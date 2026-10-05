using System.IO;
using System.Security;
using Microsoft.Win32;

namespace DevOverlay.Platform.Windows;

// Thin wrapper over the one registry value DevOverlay owns; replaced by an in-memory fake in tests.
internal interface IStartupRegistry
{
    /// <summary>Returns null when the value does not exist. Throws <see cref="InvalidDataException"/> for a non-string value.</summary>
    string? Read(string name);
    void Write(string name, string data);
    /// <summary>Removes the value if present; never touches any other value.</summary>
    void Delete(string name);
}

internal sealed class CurrentUserRunKey : IStartupRegistry
{
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        return value as string ?? throw new InvalidDataException("The startup value is not a string.");
    }

    public void Write(string name, string data)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(name, data, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

internal enum StartupState
{
    Disabled,          // no DevOverlay value
    Enabled,           // our value points at the running executable
    EnabledOtherPath,  // our value points at another DevOverlay.exe (the portable folder was moved or copied)
    Invalid,           // a value with our name exists but is not in the form we write; it is never repaired
    Unavailable        // the registry could not be read
}

internal sealed record StartupStatus(StartupState State, string? RegisteredPath = null, string? Error = null)
{
    internal bool IsEnabled => State == StartupState.Enabled;
}

/// <summary>
/// "Start DevOverlay with Windows" through the per-user Run key. The registry is the source of truth, nothing is stored in
/// settings.json, no elevation is needed, and only the value named <see cref="ValueName"/> is ever read, written or deleted.
/// </summary>
internal sealed class StartupRegistration
{
    internal const string ValueName = "DevOverlay";
    internal const string ExecutableFileName = "DevOverlay.exe";
    private readonly IStartupRegistry _registry;
    private readonly Func<string?> _currentExecutable;
    private readonly Action<string> _log;

    internal StartupRegistration(IStartupRegistry registry, Func<string?>? currentExecutable = null, Action<string>? log = null)
    {
        _registry = registry;
        _currentExecutable = currentExecutable ?? (() => Environment.ProcessPath);
        _log = log ?? (message => RuntimeDiagnostics.Write(message));
    }

    internal static string Quote(string executablePath) => "\"" + executablePath + "\"";

    // Only the exact form we write is recognised: one quoted absolute path to a file named DevOverlay.exe and nothing else.
    internal static string? ParseOwnedPath(string? value)
    {
        if (value is not { Length: > 2 } || value[0] != '"' || value[^1] != '"') return null;
        var path = value[1..^1];
        if (path.Contains('"') || !Path.IsPathFullyQualified(path)) return null;
        return string.Equals(Path.GetFileName(path), ExecutableFileName, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal StartupStatus Inspect()
    {
        string? value;
        try { value = _registry.Read(ValueName); }
        catch (InvalidDataException) { return new(StartupState.Invalid, Error: "The startup entry has an unexpected format."); }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            _log($"[StartupSetting] Read failed: {exception.GetType().Name}");
            return new(StartupState.Unavailable, Error: Describe(exception, reading: true));
        }
        if (value is null) return new(StartupState.Disabled);
        var owned = ParseOwnedPath(value);
        if (owned is null) return new(StartupState.Invalid);
        var current = _currentExecutable();
        return current is not null && SamePath(owned, current)
            ? new(StartupState.Enabled, owned) : new(StartupState.EnabledOtherPath, owned);
    }

    /// <summary>Applies the checkbox. Always returns the real registry state afterwards (with an error message on failure).</summary>
    internal StartupStatus Apply(bool enable)
    {
        string? error = null;
        try
        {
            if (enable)
            {
                var current = _currentExecutable();
                if (current is null || !Path.IsPathFullyQualified(current) ||
                    !string.Equals(Path.GetFileName(current), ExecutableFileName, StringComparison.OrdinalIgnoreCase))
                    error = "Start with Windows is only available when DevOverlay.exe is running.";
                else _registry.Write(ValueName, Quote(current));
            }
            else _registry.Delete(ValueName);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            _log($"[StartupSetting] {(enable ? "Enable" : "Disable")} failed: {exception.GetType().Name}");
            error = Describe(exception, reading: false);
        }
        var actual = Inspect();
        return error is null ? actual : actual with { Error = error };
    }

    /// <summary>
    /// A portable folder may have moved. When startup is already enabled through a DevOverlay-owned value that points at another
    /// DevOverlay.exe, point it at the running executable. Disabled, malformed or foreign values are left alone.
    /// </summary>
    internal bool RepairOwnedEntry()
    {
        var status = Inspect();
        if (status.State != StartupState.EnabledOtherPath) return false;
        var current = _currentExecutable();
        if (current is null || !Path.IsPathFullyQualified(current) ||
            !string.Equals(Path.GetFileName(current), ExecutableFileName, StringComparison.OrdinalIgnoreCase)) return false;
        try { _registry.Write(ValueName, Quote(current)); }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            _log($"[StartupSetting] Repair failed: {exception.GetType().Name}");
            return false;
        }
        _log("[StartupSetting] Updated the Windows startup entry to the current DevOverlay.exe location");
        return true;
    }

    private static bool IsRegistryFailure(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException or ArgumentException;

    private static string Describe(Exception exception, bool reading) => exception is UnauthorizedAccessException or SecurityException
        ? "Access to the Windows startup setting was denied."
        : reading ? "Could not read the Windows startup setting." : "Could not update the Windows startup setting.";
}
