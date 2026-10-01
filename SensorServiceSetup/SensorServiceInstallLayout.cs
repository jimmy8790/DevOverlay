using System.IO;

namespace DevOverlay.SensorServiceSetup;

/// <summary>
/// Fixed, administrator-owned location of the installed LocalSystem service payload.
/// The portable application folder is only an install source and is never registered as the service image.
/// </summary>
public sealed class SensorServiceInstallLayout
{
    public const string RootDirectoryName = "DevOverlaySensorService";
    public const string ExecutableName = "DevOverlay.SensorService.exe";

    // The release payload is a single self-contained exe (plus native helpers beside it); everything else in the source folder is copied as-is.
    public static readonly IReadOnlyList<string> RequiredPayloadFiles = [ExecutableName];

    public SensorServiceInstallLayout(string programFilesDirectory)
    {
        if (string.IsNullOrWhiteSpace(programFilesDirectory) || !Path.IsPathFullyQualified(programFilesDirectory))
            throw new ArgumentException("Program Files must be an absolute local path.", nameof(programFilesDirectory));
        ProgramFilesDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFilesDirectory));
        RootDirectory = Path.Combine(ProgramFilesDirectory, RootDirectoryName);
        CurrentDirectory = Path.Combine(RootDirectory, "current");
        StagingDirectory = Path.Combine(RootDirectory, "staging");
        PreviousDirectory = Path.Combine(RootDirectory, "previous");
        ExecutablePath = Path.Combine(CurrentDirectory, ExecutableName);
    }

    public string ProgramFilesDirectory { get; }
    public string RootDirectory { get; }
    public string CurrentDirectory { get; }
    public string StagingDirectory { get; }
    public string PreviousDirectory { get; }
    public string ExecutablePath { get; }

    public static SensorServiceInstallLayout ForCurrentMachine() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    public string QuotedExecutablePath => $"\"{ExecutablePath}\"";

    public bool IsInstalledImage(string? imagePath)
    {
        var path = ParseImagePath(imagePath);
        return path is not null && string.Equals(path, ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsOwnedDirectory(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(full, RootDirectory, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, CurrentDirectory, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, StagingDirectory, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, PreviousDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsInsideRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(full, RootDirectory, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the executable path from an SCM ImagePath, or null when it cannot be parsed unambiguously.</summary>
    public static string? ParseImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var value = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end <= 1) return null;
            value = value[1..end];
        }
        else if (value.Contains(' '))
        {
            // An unquoted path with spaces is resolved ambiguously by the SCM; never treat it as ours.
            return null;
        }
        return Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : null;
    }
}
