using System.Diagnostics;
using System.IO;

namespace DevOverlay.Platform.Windows;

/// <summary>Development-only state transitions; no release file logging or sample stream.</summary>
internal static class RuntimeDiagnostics
{
#if DEBUG
    private static readonly object Gate = new();
    internal static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevOverlay", "Diagnostics", $"runtime-{Environment.ProcessId}.log");
#endif

    [Conditional("DEBUG")]
    internal static void Write(string message)
    {
#if DEBUG
        Debug.WriteLine(message);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Runtime diagnostics unavailable: {exception.Message}");
        }
#endif
    }
}
