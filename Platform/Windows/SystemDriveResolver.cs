using System.IO;

namespace DevOverlay.Platform.Windows;

public static class SystemDriveResolver
{
    public static string? GetVolumeName() => FromSystemDirectory(Environment.GetFolderPath(Environment.SpecialFolder.System));

    internal static string? FromSystemDirectory(string? systemDirectory)
    {
        if (string.IsNullOrWhiteSpace(systemDirectory)) return null;
        try
        {
            var root = Path.GetPathRoot(systemDirectory);
            if (root is null || root.Length < 2 || root[1] != ':' || !char.IsLetter(root[0])) return null;
            return root[..2].ToUpperInvariant();
        }
        catch (ArgumentException) { return null; }
    }
}
