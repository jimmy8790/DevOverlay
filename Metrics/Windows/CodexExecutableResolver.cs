using System.Diagnostics;
using System.IO;

namespace DevOverlay.Metrics.Windows;

internal enum CodexLaunchKind
{
    NativeExecutable,
    CommandScript
}

internal enum CodexPathSource
{
    ProcessPath,
    UserPath,
    MachinePath
}

/// <summary>Launch description for the one App Server child; it never invokes a shell for native executables.</summary>
internal sealed record CodexLaunchCommand(
    CodexLaunchKind Kind,
    CodexPathSource Source,
    string ResolvedPath,
    string CommandProcessorPath,
    string? ChildPath = null)
{
    public string Extension => Path.GetExtension(ResolvedPath).ToLowerInvariant();

    public ProcessStartInfo CreateAppServerStartInfo()
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ApplyChildEnvironment(info);

        if (Kind == CodexLaunchKind.NativeExecutable)
        {
            info.FileName = ResolvedPath;
            info.ArgumentList.Add("app-server");
            info.ArgumentList.Add("--stdio");
            return info;
        }

        info.FileName = CommandProcessorPath;
        // cmd.exe has its own quote grammar; this exact /s /c form preserves a spaced script path and fixed arguments.
        info.Arguments = $"/d /s /c \"\"{ResolvedPath}\" app-server --stdio\"";
        return info;
    }

    /// <summary>Starts only Claude Code's documented local /usage command with fixed arguments.</summary>
    public ProcessStartInfo CreateUsageStartInfo()
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ApplyChildEnvironment(info);

        if (Kind == CodexLaunchKind.NativeExecutable)
        {
            info.FileName = ResolvedPath;
            info.ArgumentList.Add("-p");
            info.ArgumentList.Add("/usage");
            info.ArgumentList.Add("--output-format");
            info.ArgumentList.Add("json");
            return info;
        }

        // All arguments are fixed literals. Keep cmd.exe only for npm's .cmd launcher.
        info.FileName = CommandProcessorPath;
        info.Arguments = $"/d /s /c \"\"{ResolvedPath}\" -p /usage --output-format json\"";
        return info;
    }

    private void ApplyChildEnvironment(ProcessStartInfo info)
    {
        // A long-running GUI can have an older inherited PATH than the current user environment.
        // npm shims need PATH again to resolve node.exe, so pass the resolver's merged safe view to the child.
        if (!string.IsNullOrWhiteSpace(ChildPath)) info.Environment["PATH"] = ChildPath;
    }
}

/// <summary>
/// Resolves only the local Codex command using Windows-compatible PATH/PATHEXT semantics.
/// User and machine PATH are re-read so a long-running GUI can recover after an installation.
/// </summary>
internal sealed class CodexExecutableResolver
{
    private static readonly string[] SafeExtensions = [".exe", ".com", ".cmd", ".bat"];
    private readonly CodexResolverEnvironment _environment;
    private readonly string _commandName;

    public CodexExecutableResolver(CodexResolverEnvironment? environment = null) : this("codex", environment) { }

    public CodexExecutableResolver(string commandName, CodexResolverEnvironment? environment = null)
    {
        if (string.IsNullOrWhiteSpace(commandName) || commandName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A command file name is required.", nameof(commandName));
        _commandName = commandName;
        _environment = environment ?? CodexResolverEnvironment.ReadCurrent();
    }

    public CodexLaunchCommand? Resolve()
    {
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, path) in _environment.PathSources())
        {
            foreach (var directory in ParseDirectories(path, seenDirectories))
            {
                foreach (var extension in _environment.Extensions)
                {
                    var candidate = Path.Combine(directory, _commandName + extension);
                    if (!File.Exists(candidate)) continue;
                    var kind = extension is ".cmd" or ".bat"
                        ? CodexLaunchKind.CommandScript
                        : CodexLaunchKind.NativeExecutable;
                    return new CodexLaunchCommand(kind, source, Path.GetFullPath(candidate), _environment.CommandProcessorPath, _environment.MergedPath);
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> ParseDirectories(string? path, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path)) yield break;
        foreach (var rawDirectory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string normalized;
            try { normalized = Path.GetFullPath(directory); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (seen.Add(normalized)) yield return normalized;
        }
    }

}

internal sealed record CodexResolverEnvironment(
    string? ProcessPath,
    string? UserPath,
    string? MachinePath,
    string? PathExtensions,
    string? CommandProcessor)
{
    private static readonly string[] SafeExtensions = [".exe", ".com", ".cmd", ".bat"];

    public IReadOnlyList<string> Extensions => ParseExtensions(PathExtensions);
    public string MergedPath => string.Join(';', ParsePathDirectories(ProcessPath)
        .Concat(ParsePathDirectories(UserPath))
        .Concat(ParsePathDirectories(MachinePath))
        .Distinct(StringComparer.OrdinalIgnoreCase));
    public string CommandProcessorPath => string.IsNullOrWhiteSpace(CommandProcessor)
        ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
        : CommandProcessor;

    public IEnumerable<(CodexPathSource Source, string? Path)> PathSources()
    {
        yield return (CodexPathSource.ProcessPath, ProcessPath);
        yield return (CodexPathSource.UserPath, UserPath);
        yield return (CodexPathSource.MachinePath, MachinePath);
    }

    public static CodexResolverEnvironment ReadCurrent() => new(
        Environment.GetEnvironmentVariable("PATH"),
        ReadPath(EnvironmentVariableTarget.User),
        ReadPath(EnvironmentVariableTarget.Machine),
        Environment.GetEnvironmentVariable("PATHEXT"),
        Environment.GetEnvironmentVariable("ComSpec"));

    private static string? ReadPath(EnvironmentVariableTarget target)
    {
        try { return Environment.GetEnvironmentVariable("PATH", target); }
        catch (Exception exception) when (exception is PlatformNotSupportedException or System.Security.SecurityException) { return null; }
    }

    private static IEnumerable<string> ParsePathDirectories(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) yield break;
        foreach (var rawDirectory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(directory)) yield return directory;
        }
    }

    private static IReadOnlyList<string> ParseExtensions(string? pathExtensions)
    {
        var source = string.IsNullOrWhiteSpace(pathExtensions) ? string.Join(';', SafeExtensions) : pathExtensions;
        var result = new List<string>();
        foreach (var rawExtension in source.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var extension = rawExtension.StartsWith('.') ? rawExtension : "." + rawExtension;
            extension = extension.ToLowerInvariant();
            if (SafeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) && !result.Contains(extension, StringComparer.OrdinalIgnoreCase))
                result.Add(extension);
        }
        return result.Count == 0 ? SafeExtensions : result;
    }
}
