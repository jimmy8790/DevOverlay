using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace DevOverlay.Platform.Windows;

public enum PawnIoState
{
    NotInstalled,
    Outdated,
    Installed,
    Installing,
    InstallFailed,
    InstalledButSensorUnavailable,
    InstalledButDriverUnavailable,
    DriverAccessible
}

public sealed record PawnIoStatus(PawnIoState State, Version? Version = null, string? Detail = null);

/// <summary>Explicit, version-pinned PawnIO prerequisite path for LHM 0.9.6.</summary>
public sealed class PawnIoPrerequisiteService
{
    internal static readonly Version MinimumVersion = new(2, 0, 0, 0);
    internal const string InstallerUrl =
        "https://raw.githubusercontent.com/LibreHardwareMonitor/LibreHardwareMonitor/v0.9.6/LibreHardwareMonitor/Resources/PawnIO_setup.exe";
    internal const string InstallerSha256 = "A3A46226C5E2824F4CDD42BE0EECBABFC672C86F7889710F5AB1E6AD385B47A0";

    private readonly Func<Version?> _readVersion;
    private readonly Func<PawnIoDeviceAccess> _probeDevice;

    public PawnIoPrerequisiteService() : this(ReadInstalledVersion, PawnIoDeviceAccessProbe.Probe) { }

    internal PawnIoPrerequisiteService(Func<Version?> readVersion, Func<PawnIoDeviceAccess>? probeDevice = null)
    {
        _readVersion = readVersion;
        _probeDevice = probeDevice ?? PawnIoDeviceAccessProbe.Probe;
    }

    public PawnIoStatus Detect() => Classify(_readVersion());

    public PawnIoStatus Diagnose()
    {
        var installed = Detect();
        if (installed.State != PawnIoState.Installed) return installed;
        var access = _probeDevice();
        return access.IsAccessible
            ? installed with { State = PawnIoState.DriverAccessible }
            : installed with { State = PawnIoState.InstalledButDriverUnavailable,
                Detail = $"Windows error {access.Win32Error}" };
    }

    internal static PawnIoStatus Classify(Version? version) => version is null
        ? new(PawnIoState.NotInstalled)
        : version < MinimumVersion
            ? new(PawnIoState.Outdated, version)
            : new(PawnIoState.Installed, version);

    public async Task<PawnIoStatus> InstallAsync(CancellationToken cancellationToken = default)
    {
        // This method is called only from the Settings button, never startup or polling.
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"DevOverlay-PawnIO-{Guid.NewGuid():N}.exe");
        try
        {
            using (var client = new HttpClient())
            using (var response = await client.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var destination = File.Create(temporaryPath);
                await response.Content.CopyToAsync(destination, cancellationToken);
            }

            await using (var installer = File.OpenRead(temporaryPath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(installer, cancellationToken));
                if (!string.Equals(actualHash, InstallerSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new(PawnIoState.InstallFailed, Detail: "Installer integrity check failed; nothing was executed.");
                }
            }

            using var process = Process.Start(new ProcessStartInfo(temporaryPath, "-install")
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process is null) return new(PawnIoState.InstallFailed, Detail: "Installer did not start.");
            await process.WaitForExitAsync(cancellationToken);
            var status = Detect();
            return EvaluateInstallerResult(process.ExitCode, status);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new(PawnIoState.InstallFailed, Detail: "Administrator approval was cancelled.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(PawnIoState.InstallFailed, Detail: exception.Message);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static PawnIoStatus EvaluateInstallerResult(int exitCode, PawnIoStatus detected) =>
        exitCode == 0 && detected.State == PawnIoState.Installed
            ? detected
            : new(PawnIoState.InstallFailed, detected.Version,
                $"Installer exit code {exitCode}; detected state: {detected.State}.");

    private static Version? ReadInstalledVersion()
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(keyPath);
            if (Version.TryParse(key?.GetValue("DisplayVersion") as string, out var version)) return version;
        }
        return null;
    }
}
