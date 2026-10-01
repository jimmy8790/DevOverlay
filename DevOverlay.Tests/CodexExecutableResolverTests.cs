using DevOverlay.Metrics.Windows;
using System.Diagnostics;
using Xunit;

namespace DevOverlay.Tests;

public sealed class CodexExecutableResolverTests
{
    [Fact]
    public void ProcessPath_ResolvesNativeExecutableUsingPathextOrder()
    {
        using var directory = new TemporaryDirectory();
        var executable = CreateCommand(directory.Path, "codex.exe");

        var command = CreateResolver(processPath: directory.Path, pathExtensions: ".CMD;.EXE").Resolve();

        Assert.NotNull(command);
        Assert.Equal(CodexLaunchKind.NativeExecutable, command.Kind);
        Assert.Equal(CodexPathSource.ProcessPath, command.Source);
        Assert.Equal(executable, command.ResolvedPath);
    }

    [Theory]
    [InlineData("codex.cmd")]
    [InlineData("codex.bat")]
    public void CommandScripts_UseComSpecWithQuotedFixedAppServerArguments(string filename)
    {
        using var directory = new TemporaryDirectory("Codex resolver spaces");
        var script = CreateCommand(directory.Path, filename);
        var command = CreateResolver(processPath: directory.Path, pathExtensions: ".EXE;.CMD;.BAT").Resolve();

        var info = Assert.IsType<CodexLaunchCommand>(command).CreateAppServerStartInfo();

        Assert.Equal(CodexLaunchKind.CommandScript, command.Kind);
        Assert.Equal(script, command.ResolvedPath);
        Assert.Equal("C:\\Windows\\System32\\cmd.exe", info.FileName);
        Assert.Equal($"/d /s /c \"\"{script}\" app-server --stdio\"", info.Arguments);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }

    [Fact]
    public void UserAndMachinePath_AreUsedWhenGuiProcessPathIsStale()
    {
        using var user = new TemporaryDirectory();
        using var machine = new TemporaryDirectory();
        var expected = CreateCommand(user.Path, "codex.exe");
        CreateCommand(machine.Path, "codex.cmd");

        var command = CreateResolver(processPath: null, userPath: user.Path, machinePath: machine.Path).Resolve();

        Assert.NotNull(command);
        Assert.Equal(CodexPathSource.UserPath, command.Source);
        Assert.Equal(expected, command.ResolvedPath);
    }

    [Fact]
    public void DuplicateAndMalformedPathSegments_AreHarmlessAndMissingCommandReturnsNull()
    {
        using var directory = new TemporaryDirectory();
        var expected = CreateCommand(directory.Path, "codex.com");
        var path = $";\"{directory.Path}\";{directory.Path};\0;";

        var command = CreateResolver(processPath: path, pathExtensions: ".COM;.EXE").Resolve();
        var missing = CreateResolver(processPath: directory.Path, pathExtensions: ".EXE").Resolve();

        Assert.NotNull(command);
        Assert.Equal(expected, command.ResolvedPath);
        Assert.Null(missing);
    }

    [Fact]
    public void NativeExecutable_LaunchesDirectlyWithRedirectedStdio()
    {
        using var directory = new TemporaryDirectory();
        var executable = CreateCommand(directory.Path, "codex.exe");
        var command = Assert.IsType<CodexLaunchCommand>(CreateResolver(processPath: directory.Path).Resolve());

        var info = command.CreateAppServerStartInfo();

        Assert.Equal(executable, info.FileName);
        Assert.Equal(["app-server", "--stdio"], info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }

    [Fact]
    public void ClaudeCommandScript_UsesComSpecFixedUsageArgumentsAndMergedChildPath()
    {
        using var process = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        using var machine = new TemporaryDirectory();
        var script = CreateCommand(user.Path, "claude.cmd");
        var resolver = new CodexExecutableResolver("claude", new CodexResolverEnvironment(process.Path, user.Path, machine.Path,
            ".CMD;.EXE", "C:\\Windows\\System32\\cmd.exe"));

        var command = Assert.IsType<CodexLaunchCommand>(resolver.Resolve());
        var info = command.CreateUsageStartInfo();

        Assert.Equal(script, command.ResolvedPath);
        Assert.Equal("C:\\Windows\\System32\\cmd.exe", info.FileName);
        Assert.Equal($"/d /s /c \"\"{script}\" -p /usage --output-format json\"", info.Arguments);
        Assert.Equal($"{process.Path};{user.Path};{machine.Path}", info.Environment["PATH"]);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }

    [Fact]
    public async Task CommandScriptLaunch_PreservesRedirectedStdout()
    {
        using var directory = new TemporaryDirectory("Codex script stdio " + Guid.NewGuid().ToString("N"));
        var script = Path.Combine(directory.Path, "codex.cmd");
        File.WriteAllText(script, "@echo off\r\necho {\"transport\":\"stdio\"}\r\n");
        var command = Assert.IsType<CodexLaunchCommand>(CreateResolver(processPath: directory.Path).Resolve());
        using var process = Process.Start(command.CreateAppServerStartInfo());

        Assert.NotNull(process);
        var line = await process.StandardOutput.ReadLineAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(string.Equals("{\"transport\":\"stdio\"}", line, StringComparison.Ordinal), error);
        Assert.Equal(0, process.ExitCode);
    }

    private static CodexExecutableResolver CreateResolver(string? processPath = null, string? userPath = null,
        string? machinePath = null, string? pathExtensions = null) => new(new CodexResolverEnvironment(
            processPath, userPath, machinePath, pathExtensions ?? ".EXE;.COM;.CMD;.BAT", "C:\\Windows\\System32\\cmd.exe"));

    private static string CreateCommand(string directory, string filename)
    {
        var path = Path.Combine(directory, filename);
        File.WriteAllText(path, string.Empty);
        return Path.GetFullPath(path);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string? suffix = null)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DevOverlay.Tests", suffix ?? Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
