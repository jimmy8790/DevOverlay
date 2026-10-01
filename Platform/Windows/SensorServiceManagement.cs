using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DevOverlay.SensorHostProtocol;

namespace DevOverlay.Platform.Windows;

internal enum SensorServiceAction { Install, Repair, Uninstall }
internal readonly record struct SensorServiceActionResult(bool Succeeded, bool ElevationCancelled, string? Detail);

internal interface ISensorServiceInstallerLauncher
{
    Process? Start(ProcessStartInfo info);
}

internal sealed class SensorServiceInstallerLauncher : ISensorServiceInstallerLauncher
{
    public Process? Start(ProcessStartInfo info) => Process.Start(info);
}

internal sealed class SensorServiceManagement(ISensorServiceInstallerLauncher? launcher = null, string? executablePath = null)
{
    private readonly ISensorServiceInstallerLauncher _launcher = launcher ?? new SensorServiceInstallerLauncher();
    private readonly string _executablePath = executablePath ?? ServiceExecutablePath;

    internal static string ServiceExecutablePath => Path.Combine(AppContext.BaseDirectory,
        "SensorService", "DevOverlay.SensorService.exe");

    internal static ProcessStartInfo CreateLaunchInfo(string executablePath, SensorServiceAction action)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add(action switch
        {
            SensorServiceAction.Install => "--install",
            SensorServiceAction.Repair => "--repair",
            SensorServiceAction.Uninstall => "--uninstall",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        });
        return info;
    }

    public async Task<SensorServiceActionResult> ExecuteAsync(SensorServiceAction action, CancellationToken token = default)
    {
        if (!File.Exists(_executablePath)) return new(false, false, "Sensor Service executable is missing from this build.");
        var info = CreateLaunchInfo(_executablePath, action);
        try
        {
            using var process = await Task.Run(() => _launcher.Start(info), token);
            if (process is null) return new(false, false, "Sensor Service installer did not start.");
            await process.WaitForExitAsync(token);
            return process.ExitCode == 0
                ? new(true, false, null)
                : new(false, false, $"Sensor Service {action} failed (exit code {process.ExitCode}). See Windows Application event log.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { return new(false, true, "Administrator approval was cancelled."); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        { return new(false, false, exception.Message); }
    }
}
