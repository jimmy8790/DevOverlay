using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class InteractiveInstanceGateTests
{
    [Fact]
    public void FirstGuiLeaseWinsSecondIsRejectedAndDisposalAllowsTheNextLaunch()
    {
        var sid = "S-1-5-21-" + Guid.NewGuid().ToString("N");
        var first = InteractiveInstanceGate.Acquire(() => sid, OpenMutex);
        Assert.Equal(InteractiveInstanceState.Primary, first.State);
        Assert.True(first.CanStartInteractiveSession);

        using var second = InteractiveInstanceGate.Acquire(() => sid, OpenMutex);
        Assert.Equal(InteractiveInstanceState.Secondary, second.State);
        Assert.False(second.CanStartInteractiveSession);

        first.Dispose();
        using var restarted = InteractiveInstanceGate.Acquire(() => sid, OpenMutex);
        Assert.Equal(InteractiveInstanceState.Primary, restarted.State);
    }

    [Fact]
    public void ClaudeStatusLineInvocationBypassesTheInteractiveGuiGate()
    {
        Assert.False(DevOverlayInvocation.IsInteractiveGuiInvocation([ClaudeStatusLineBridge.Argument]));
        Assert.False(DevOverlayInvocation.IsInteractiveGuiInvocation(["--CLAUDE-STATUS-LINE"]));
        Assert.True(DevOverlayInvocation.IsInteractiveGuiInvocation([]));
    }

    [Fact]
    public void RunningGuiDoesNotClassifyClaudeStatusLineAsASecondGuiLaunch()
    {
        var sid = "S-1-5-21-" + Guid.NewGuid().ToString("N");
        using var gui = InteractiveInstanceGate.Acquire(() => sid, OpenMutex);
        Assert.Equal(InteractiveInstanceState.Primary, gui.State);
        Assert.False(DevOverlayInvocation.IsInteractiveGuiInvocation([ClaudeStatusLineBridge.Argument]));
    }

    [Fact]
    public void StatusLineInvocationRemainsTheExactDocumentedClaudeArgument()
    {
        Assert.True(ClaudeStatusLineBridge.IsInvocation(["--before", ClaudeStatusLineBridge.Argument, "--after"]));
        Assert.False(ClaudeStatusLineBridge.IsInvocation(["--claude-status-line=value"]));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--claude-status-lines")]
    [InlineData("--claude-status-line=value")]
    public void UnknownOrMalformedArgumentsKeepExistingInteractiveGuiBehavior(string argument)
    {
        Assert.True(DevOverlayInvocation.IsInteractiveGuiInvocation([argument]));
    }

    [Fact]
    public void MutexIdentityIsStableAcrossPathAndVersionChanges()
    {
        const string sid = "S-1-5-21-123456789-123456789-123456789-1001";
        Assert.Equal(@"Local\DevOverlay.Interactive.S-1-5-21-123456789-123456789-123456789-1001",
            InteractiveInstanceGate.BuildMutexName(sid));
    }

    [Fact]
    public void MutexCreationFailureDoesNotCrashOrBlockTheGuiLaunch()
    {
        var gate = InteractiveInstanceGate.Acquire(() => "S-1-5-21-test", _ => throw new UnauthorizedAccessException());
        Assert.Equal(InteractiveInstanceState.Unavailable, gate.State);
        Assert.True(gate.CanStartInteractiveSession);
        Assert.Equal(nameof(UnauthorizedAccessException), gate.Error);
    }

    private static (Mutex Mutex, bool CreatedNew) OpenMutex(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        return (mutex, createdNew);
    }
}
