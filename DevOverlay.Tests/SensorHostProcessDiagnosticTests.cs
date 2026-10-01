using System.Diagnostics;
using System.IO.Pipes;
using DevOverlay.Platform.Windows;
using DevOverlay.SensorHostProtocol;
using Xunit;
using Xunit.Abstractions;

namespace DevOverlay.Tests;

public sealed class SensorHostProcessDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NonElevatedHelper_ReportsItsActualPawnIoState_WithoutUac()
    {
        if (Environment.GetEnvironmentVariable("DEVOVERLAY_RUN_SENSORHOST_DIAGNOSTIC") != "1") return;
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string helper = Path.Combine(repository, "bin", configuration, "net8.0-windows10.0.19041.0",
            "SensorHost", "DevOverlay.SensorHost.exe");
        Assert.True(File.Exists(helper), $"SensorHost build output missing: {helper}");
        string name = SensorHostClient.CreatePipeName();
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            SensorHostClient.PipeSecurityOptions);
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var child = Process.Start(start);
        Assert.NotNull(child);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.WaitForConnectionAsync(timeout.Token);
        await SensorHostWire.WriteRequestAsync(pipe,
            new SensorHostRequest(SensorHostWire.Version, SensorHostOperation.Hello), timeout.Token);
        var hello = await SensorHostWire.ReadResponseAsync(pipe, timeout.Token);
        output.WriteLine($"Non-elevated SensorHost: {hello.State}; {hello.Detail}");
        Assert.Equal(SensorHostMessageKind.Hello, hello.Kind);
        await SensorHostWire.WriteRequestAsync(pipe,
            new SensorHostRequest(SensorHostWire.Version, SensorHostOperation.GetCpuPackageSnapshot), timeout.Token);
        var snapshot = await SensorHostWire.ReadResponseAsync(pipe, timeout.Token);
        output.WriteLine($"CPU Package: {snapshot.TemperatureCelsius}°C; {snapshot.PackagePowerWatts}W; {snapshot.State}");
        Assert.Equal(SensorHostMessageKind.CpuPackageSnapshot, snapshot.Kind);
        await SensorHostWire.WriteRequestAsync(pipe,
            new SensorHostRequest(SensorHostWire.Version, SensorHostOperation.Shutdown), timeout.Token);
        Assert.Equal(SensorHostMessageKind.Goodbye,
            (await SensorHostWire.ReadResponseAsync(pipe, timeout.Token)).Kind);
        await child.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, child.ExitCode);
    }
}
