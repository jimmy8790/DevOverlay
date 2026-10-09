using System.Diagnostics;
using System.IO;
using System.Text;
using DevOverlay.Platform.Windows;
using Xunit;

namespace DevOverlay.Tests;

// The gate is exercised against a mutex that is really owned by another process, using a throw-away child process instead of
// the WPF app (which would touch the tray, the global hotkey and the user's settings). Nothing here sleeps: the child
// announces readiness on stdout and the parent waits for that line or for the process to exit.
public sealed class InteractiveInstanceProcessTests
{
    private static string NewSid() => "S-1-5-21-" + Guid.NewGuid().ToString("N");

    private static (Mutex Mutex, bool CreatedNew) OpenMutex(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        return (mutex, createdNew);
    }

    private sealed class MutexHolder : IDisposable
    {
        private readonly Process _process;
        internal MutexHolder(string mutexName)
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            Assert.True(File.Exists(powershell), "Windows PowerShell is required for the process-level test.");
            var script = "$created = $false; " +
                $"$m = New-Object System.Threading.Mutex($true, '{mutexName}', [ref]$created); " +
                "[Console]::Out.WriteLine('ready:' + $created); [Console]::Out.Flush(); [void][Console]::In.ReadLine()";
            var info = new ProcessStartInfo(powershell,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            _process = Process.Start(info) ?? throw new InvalidOperationException("The child process did not start.");
            var line = Task.Run(() => _process.StandardOutput.ReadLine());
            Assert.True(line.Wait(TimeSpan.FromSeconds(60)), "The child process never reported that it owns the mutex.");
            Assert.Equal("ready:True", line.Result);
        }
        internal bool IsRunning => !_process.HasExited;
        internal void ExitNormally()
        {
            _process.StandardInput.WriteLine();
            Assert.True(_process.WaitForExit(30_000), "The child process did not exit.");
        }
        internal void Kill()
        {
            _process.Kill(entireProcessTree: true);
            Assert.True(_process.WaitForExit(30_000), "The child process did not die.");
        }
        public void Dispose()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }

    [Fact]
    public void GuiOwnedByAnotherProcessSurvivesTheSecondLaunchIsRejectedAndRelaunchWorksAfterItExits()
    {
        var sid = NewSid();
        using var firstGui = new MutexHolder(InteractiveInstanceGate.BuildMutexName(sid));

        using (var secondLaunch = InteractiveInstanceGate.Acquire(() => sid, OpenMutex))
        {
            Assert.Equal(InteractiveInstanceState.Secondary, secondLaunch.State);
            Assert.False(secondLaunch.CanStartInteractiveSession);
        }
        Assert.True(firstGui.IsRunning); // rejecting the second launch never disturbs the first process

        firstGui.ExitNormally();
        using var relaunch = InteractiveInstanceGate.Acquire(() => sid, OpenMutex);
        Assert.Equal(InteractiveInstanceState.Primary, relaunch.State);
    }

    [Fact]
    public void CrashedOwnerNeverLeavesAStaleLockThatBlocksTheNextLaunch()
    {
        var sid = NewSid();
        using var firstGui = new MutexHolder(InteractiveInstanceGate.BuildMutexName(sid));
        using (var rejected = InteractiveInstanceGate.Acquire(() => sid, OpenMutex))
            Assert.Equal(InteractiveInstanceState.Secondary, rejected.State);

        firstGui.Kill(); // no graceful release: the OS must clean the named object up by itself
        using var relaunch = InteractiveInstanceGate.Acquire(() => sid, OpenMutex);
        Assert.Equal(InteractiveInstanceState.Primary, relaunch.State);
    }

    [Fact]
    public void RejectedLaunchDoesNotKeepTheNamedObjectAliveAfterTheOwnerExits()
    {
        // A rejected launch must close its handle; otherwise the lock would outlive the real owner.
        var sid = NewSid();
        using var firstGui = new MutexHolder(InteractiveInstanceGate.BuildMutexName(sid));
        var rejected = InteractiveInstanceGate.Acquire(() => sid, OpenMutex);
        Assert.Equal(InteractiveInstanceState.Secondary, rejected.State);
        rejected.Dispose();
        firstGui.ExitNormally();

        using var next = InteractiveInstanceGate.Acquire(() => sid, OpenMutex);
        Assert.Equal(InteractiveInstanceState.Primary, next.State);
    }

    [Fact]
    public void DifferentWindowsUsersNeverBlockEachOther()
    {
        using var alice = InteractiveInstanceGate.Acquire(() => NewSid(), OpenMutex);
        using var bob = InteractiveInstanceGate.Acquire(() => NewSid(), OpenMutex);
        Assert.Equal(InteractiveInstanceState.Primary, alice.State);
        Assert.Equal(InteractiveInstanceState.Primary, bob.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingUserSidFailsOpenInsteadOfBlockingTheLaunch(string? sid)
    {
        using var gate = InteractiveInstanceGate.Acquire(() => sid, name => throw new InvalidOperationException("must not be reached: " + name));
        Assert.Equal(InteractiveInstanceState.Unavailable, gate.State);
        Assert.True(gate.CanStartInteractiveSession);
    }

    [Fact]
    public void ProviderFailureForTheUserSidAlsoFailsOpen()
    {
        using var gate = InteractiveInstanceGate.Acquire(() => throw new InvalidOperationException("no identity"), OpenMutex);
        Assert.Equal(InteractiveInstanceState.Unavailable, gate.State);
        Assert.True(gate.CanStartInteractiveSession);
        Assert.Equal(nameof(InvalidOperationException), gate.Error);
    }

    [Fact]
    public void TheWindowsStartupCommandIsAPlainGuiLaunchWithoutArguments()
    {
        // Windows executes the Run value as a command line. StartupRegistration writes only the quoted executable path,
        // so nothing follows the closing quote and the launch must be classified as an ordinary interactive GUI start.
        var command = StartupRegistration.Quote(@"C:\Path With Spaces\DevOverlay\DevOverlay.exe");
        var arguments = command[(command.LastIndexOf('"') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Empty(arguments);
        Assert.True(DevOverlayInvocation.IsInteractiveGuiInvocation(arguments));
    }

    [Fact]
    public void StatusLineHelperRunsWhileAnotherProcessOwnsTheGuiLock()
    {
        var sid = NewSid();
        using var gui = new MutexHolder(InteractiveInstanceGate.BuildMutexName(sid));
        // The helper path never calls Acquire: classification alone decides, so it cannot be rejected by the owner.
        Assert.False(DevOverlayInvocation.IsInteractiveGuiInvocation(["--claude-status-line"]));
        using var secondGui = InteractiveInstanceGate.Acquire(() => sid, OpenMutex);
        Assert.Equal(InteractiveInstanceState.Secondary, secondGui.State);
        Assert.True(gui.IsRunning);
    }
}
