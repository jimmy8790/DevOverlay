using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace DevOverlay.Platform.Windows;

internal enum PresentMonRunState { NotInstalled, Stopped, Starting, Running, Unavailable }

internal readonly record struct PresentMonStatus(PresentMonRunState State, string? Version = null);

/// <summary>The official installer owns the service; DevOverlay only observes it.</summary>
internal static class PresentMonServiceInfo
{
    public const string ServiceName = "PresentMonSharedService";
    public const string OfficialReleaseUrl = "https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0";

    public static PresentMonStatus Read()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            var state = service.Status switch
            {
                ServiceControllerStatus.Running => PresentMonRunState.Running,
                ServiceControllerStatus.StartPending => PresentMonRunState.Starting,
                ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending => PresentMonRunState.Stopped,
                _ => PresentMonRunState.Unavailable
            };
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Intel", "PresentMonSharedService", "PresentMonAPI2.dll");
            var fileVersion = File.Exists(path) ? FileVersionInfo.GetVersionInfo(path) : null;
            var version = fileVersion?.FileVersion;
            if (state == PresentMonRunState.Running && fileVersion is not null &&
                (fileVersion.FileMajorPart != 2 || fileVersion.FileMinorPart != 6 || fileVersion.FileBuildPart != 0))
                return new PresentMonStatus(PresentMonRunState.Unavailable, version);
            return new PresentMonStatus(state, version);
        }
        catch (InvalidOperationException) { return new PresentMonStatus(PresentMonRunState.NotInstalled); }
        catch (System.ComponentModel.Win32Exception) { return new PresentMonStatus(PresentMonRunState.Unavailable); }
    }
}
