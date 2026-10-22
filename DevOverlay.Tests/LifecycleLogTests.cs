using System.IO;
using DevOverlay.Platform.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class LifecycleLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DevOverlay-tests", Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_directory, "Diagnostics", "lifecycle.log");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Clock { public DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero); }

    [Fact]
    public void WriteCreatesTheDirectoryAndRecordsTimestampPidAndMessage()
    {
        var clock = new Clock();
        var log = new LifecycleLog(LogPath, () => clock.Now);
        log.Write("[Startup] hello");
        var line = Assert.Single(File.ReadAllLines(LogPath));
        Assert.StartsWith("2026-10-22T12:00:00", line);
        Assert.Contains($"[{Environment.ProcessId}:", line);
        Assert.EndsWith("[Startup] hello", line);
    }

    [Fact]
    public void FileIsRotatedOnceItExceedsTheLimitSoItNeverGrowsWithoutBound()
    {
        var log = new LifecycleLog(LogPath, maxBytes: 400);
        for (var i = 0; i < 40; i++) log.Write($"[Test] line {i:D3} padding padding padding");
        Assert.True(File.Exists(LogPath + ".old"));
        Assert.True(new FileInfo(LogPath).Length < 400 + 200);
        Assert.Contains("line 039", File.ReadAllText(LogPath));
    }

    [Fact]
    public void RepeatedFaultsAreThrottledAndReportTheSuppressedCount()
    {
        var clock = new Clock();
        var log = new LifecycleLog(LogPath, () => clock.Now);
        var interval = TimeSpan.FromMinutes(5);
        log.WriteThrottled("provider:X", "[ProviderFaulted] X: boom", interval);
        clock.Now += TimeSpan.FromSeconds(1); log.WriteThrottled("provider:X", "[ProviderFaulted] X: boom", interval);
        clock.Now += TimeSpan.FromSeconds(1); log.WriteThrottled("provider:X", "[ProviderFaulted] X: boom", interval);
        log.WriteThrottled("provider:Y", "[ProviderFaulted] Y: other", interval); // a different key is independent
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
        clock.Now += interval;
        log.WriteThrottled("provider:X", "[ProviderFaulted] X: boom", interval);
        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("[ProviderFaulted] X: boom (+2 similar suppressed)", lines[^1]);
    }

    [Fact]
    public void AnUnwritableLocationNeverThrows()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "file-not-directory");
        File.WriteAllText(blocker, "x");
        var log = new LifecycleLog(Path.Combine(blocker, "lifecycle.log"));
        log.Write("must not throw");
        log.WriteThrottled("k", "must not throw", TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void FatalEntryRecordsTheExceptionAndTheCurrentContext()
    {
        var log = new LifecycleLog(LogPath);
        LifecycleLog.SetContext("test.target", "4242:game.exe");
        try { log.WriteFatal(new InvalidOperationException("boom"), isTerminating: true); }
        finally { LifecycleLog.SetContext("test.target", null); }
        var text = File.ReadAllText(LogPath);
        Assert.Contains("[Fatal] Terminating=True", text);
        Assert.Contains("test.target=4242:game.exe", text);
        Assert.Contains("InvalidOperationException: boom", text);
    }

    [Fact]
    public void ContextIsEmptyWhenNothingIsRegistered()
    {
        LifecycleLog.SetContext("test.device", "0x1");
        LifecycleLog.SetContext("test.device", null);
        Assert.DoesNotContain("test.device", LifecycleLog.DescribeContext());
    }

    private string MarkerPath => Path.Combine(_directory, "Diagnostics", "session.marker");

    [Fact]
    public void CleanExitLeavesNothingBehindSoTheNextStartReportsNothing()
    {
        var first = new SessionMarker(MarkerPath);
        first.Begin(1111, "0.3.2.0");
        Assert.True(File.Exists(MarkerPath));
        first.MarkClean();
        Assert.False(File.Exists(MarkerPath));
        Assert.Null(new SessionMarker(MarkerPath).DescribePreviousUncleanSession(2222, _ => false));
    }

    [Fact]
    public void LeftoverMarkerFromADeadProcessIsReportedWithItsLastContext()
    {
        var clock = new Clock();
        var crashed = new SessionMarker(MarkerPath, () => clock.Now);
        LifecycleLog.SetContext("test.target", "4242:game.exe");
        try
        {
            crashed.Begin(1111, "0.3.2.0");
            clock.Now += TimeSpan.FromMinutes(7);
            crashed.Heartbeat(); // the process then dies without MarkClean
        }
        finally { LifecycleLog.SetContext("test.target", null); }

        var report = new SessionMarker(MarkerPath).DescribePreviousUncleanSession(2222, _ => false);
        Assert.NotNull(report);
        Assert.Contains("pid=1111", report);
        Assert.Contains("version=0.3.2.0", report);
        Assert.Contains("started=2026-10-22T12:00:00", report);
        Assert.Contains("lastHeartbeat=2026-10-22T12:07:00", report);
        Assert.Contains("test.target=4242:game.exe", report);
    }

    [Fact]
    public void MarkerOfTheCurrentOrAStillRunningProcessIsNotAnUncleanSession()
    {
        new SessionMarker(MarkerPath).Begin(1111, "v");
        Assert.Null(new SessionMarker(MarkerPath).DescribePreviousUncleanSession(1111, _ => false)); // same process
        Assert.Null(new SessionMarker(MarkerPath).DescribePreviousUncleanSession(2222, pid => pid == 1111)); // still alive
    }

    [Fact]
    public void CorruptOrUnwritableMarkerNeverThrows()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        File.WriteAllText(MarkerPath, "garbage without pid");
        Assert.Equal("unreadable marker", new SessionMarker(MarkerPath).DescribePreviousUncleanSession(2222, _ => false));

        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "x");
        var unwritable = new SessionMarker(Path.Combine(blocker, "session.marker"));
        unwritable.Begin(1, "v");
        unwritable.Heartbeat();
        unwritable.MarkClean();
    }
}
