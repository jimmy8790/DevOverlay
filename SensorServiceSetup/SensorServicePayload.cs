using System.IO;

namespace DevOverlay.SensorServiceSetup;

/// <summary>File operations for the installed payload. Only the directories defined by the layout are ever deleted.</summary>
public sealed class SensorServicePayload(SensorServiceInstallLayout layout)
{
    private const int DeleteAttempts = 10;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(500);

    public string ValidateSource(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Path.IsPathFullyQualified(sourceDirectory))
            throw new ArgumentException("The service payload source must be an absolute path.", nameof(sourceDirectory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        if (layout.IsInsideRoot(full))
            throw new InvalidOperationException("Install and Repair must run from the DevOverlay package, not from the installed service directory.");
        var directory = new DirectoryInfo(full);
        if (!directory.Exists) throw new DirectoryNotFoundException($"Service payload not found: {full}");
        RejectReparsePoint(directory);
        foreach (var name in SensorServiceInstallLayout.RequiredPayloadFiles)
        {
            var file = new FileInfo(Path.Combine(full, name));
            if (!file.Exists) throw new FileNotFoundException("Service payload is incomplete.", file.FullName);
            RejectReparsePoint(file);
        }
        return full;
    }

    public void Stage(string validatedSource)
    {
        DeleteOwned(layout.StagingDirectory);
        Directory.CreateDirectory(layout.StagingDirectory);
        CopyTree(validatedSource, layout.StagingDirectory);
        foreach (var name in SensorServiceInstallLayout.RequiredPayloadFiles)
            if (!File.Exists(Path.Combine(layout.StagingDirectory, name)))
                throw new FileNotFoundException("Staged service payload is incomplete.", name);
    }

    /// <summary>Replaces current with staging; the previous payload is kept until the caller confirms the service started.</summary>
    public void Activate()
    {
        DeleteOwned(layout.PreviousDirectory);
        var hadCurrent = Directory.Exists(layout.CurrentDirectory);
        if (hadCurrent) Directory.Move(layout.CurrentDirectory, layout.PreviousDirectory);
        try
        {
            Directory.Move(layout.StagingDirectory, layout.CurrentDirectory);
        }
        catch
        {
            if (hadCurrent && !Directory.Exists(layout.CurrentDirectory))
                Directory.Move(layout.PreviousDirectory, layout.CurrentDirectory);
            throw;
        }
    }

    public void RemoveAll() => DeleteOwned(layout.RootDirectory);

    public void TryRemoveStaging() => TryDelete(layout.StagingDirectory);

    public void TryRemovePrevious() => TryDelete(layout.PreviousDirectory);

    public void TryRemoveAll() => TryDelete(layout.RootDirectory);

    internal static string ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Unexpected payload path: {relativePath}");
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var destination = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!destination.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Payload path escapes the service directory: {relativePath}");
        return destination;
    }

    internal void DeleteOwned(string path)
    {
        if (!layout.IsOwnedDirectory(path))
            throw new InvalidOperationException($"Refusing to delete a directory not owned by the Sensor Service install: {path}");
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) return;
        RejectReparsePoint(directory);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(directory.FullName, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < DeleteAttempts)
            {
                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    private static void CopyTree(string sourceRoot, string targetRoot)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(sourceRoot));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                RejectReparsePoint(entry);
                var destination = ResolveInside(targetRoot, Path.GetRelativePath(sourceRoot, entry.FullName));
                if (entry is DirectoryInfo directory)
                {
                    Directory.CreateDirectory(destination);
                    pending.Push(directory);
                }
                else
                {
                    File.Copy(entry.FullName, destination, overwrite: false);
                }
            }
        }
    }

    private void TryDelete(string path)
    {
        try { DeleteOwned(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or InvalidDataException) { }
    }

    private static void RejectReparsePoint(FileSystemInfo info)
    {
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"Refusing to copy or delete a reparse point: {info.FullName}");
    }
}
