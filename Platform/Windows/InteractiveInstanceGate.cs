using System.Security.Principal;
using DevOverlay.Metrics.Windows;

namespace DevOverlay.Platform.Windows;

/// <summary>
/// Distinguishes the documented Claude status-line helper invocation from an interactive WPF launch.
/// Unknown arguments deliberately remain interactive launches so existing application behavior is preserved.
/// </summary>
internal static class DevOverlayInvocation
{
    internal static bool IsInteractiveGuiInvocation(IEnumerable<string> arguments) => !ClaudeStatusLineBridge.IsInvocation(arguments);
}

internal enum InteractiveInstanceState
{
    Primary,
    Secondary,
    Unavailable
}

/// <summary>
/// Keeps one interactive DevOverlay process per Windows user and logon session. The local namespace keeps
/// separate sessions isolated, while the SID prevents unrelated users in a shared session from colliding.
/// </summary>
internal sealed class InteractiveInstanceGate : IDisposable
{
    private const string MutexPrefix = @"Local\DevOverlay.Interactive.";
    private readonly Mutex? _mutex;
    private readonly bool _ownsMutex;
    private bool _disposed;

    private InteractiveInstanceGate(InteractiveInstanceState state, Mutex? mutex = null, bool ownsMutex = false, string? error = null)
    {
        State = state;
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        Error = error;
    }

    internal InteractiveInstanceState State { get; }
    internal string? Error { get; }
    internal bool CanStartInteractiveSession => State != InteractiveInstanceState.Secondary;

    internal static InteractiveInstanceGate Acquire() => Acquire(GetCurrentUserSid, CreateMutex);

    // The delegates make the platform boundary deterministic in tests without touching a real GUI process.
    internal static InteractiveInstanceGate Acquire(Func<string?> userSidProvider, Func<string, (Mutex Mutex, bool CreatedNew)> mutexFactory)
    {
        try
        {
            var userSid = userSidProvider();
            if (string.IsNullOrWhiteSpace(userSid))
                return new InteractiveInstanceGate(InteractiveInstanceState.Unavailable, error: "Windows user SID was unavailable.");

            var (mutex, createdNew) = mutexFactory(BuildMutexName(userSid));
            if (!createdNew)
            {
                // A non-owner must not keep the named object alive after the primary process exits.
                mutex.Dispose();
                return new InteractiveInstanceGate(InteractiveInstanceState.Secondary);
            }

            return new InteractiveInstanceGate(InteractiveInstanceState.Primary, mutex, ownsMutex: true);
        }
        catch (Exception exception)
        {
            // Failing open is preferable to making the user's desktop app impossible to start because the OS could not
            // create a synchronization object. The exception type only is written by App; no account details are logged.
            return new InteractiveInstanceGate(InteractiveInstanceState.Unavailable, error: exception.GetType().Name);
        }
    }

    internal static string BuildMutexName(string userSid) => MutexPrefix + userSid;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mutex is null) return;
        try
        {
            if (_ownsMutex) _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The process is exiting or the mutex was already abandoned; disposing the handle is still safe.
        }
        finally
        {
            _mutex.Dispose();
        }
    }

    private static string? GetCurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value;

    private static (Mutex Mutex, bool CreatedNew) CreateMutex(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        return (mutex, createdNew);
    }
}
