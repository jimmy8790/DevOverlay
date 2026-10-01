using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;
using DevOverlay.SensorHostProtocol;
using Xunit;

namespace DevOverlay.Tests;

public sealed class SensorHostPrototypeTests
{
    [Fact]
    public async Task Protocol_RoundTripsFixedOperationsAndNullableSnapshot()
    {
        using var stream = new MemoryStream();
        await SensorHostWire.WriteRequestAsync(stream,
            new SensorHostRequest(SensorHostWire.Version, SensorHostOperation.GetCpuPackageSnapshot), CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(SensorHostOperation.GetCpuPackageSnapshot,
            (await SensorHostWire.ReadRequestAsync(stream, CancellationToken.None)).Operation);

        stream.SetLength(0);
        stream.Position = 0;
        var sent = new SensorHostResponse(SensorHostWire.Version, SensorHostMessageKind.CpuPackageSnapshot,
            SensorHostState.CpuSensorsUnavailable, 61.5, null, DateTimeOffset.UtcNow,
            TemperatureSensorFound: true, PowerSensorFound: true);
        await SensorHostWire.WriteResponseAsync(stream, sent, CancellationToken.None);
        stream.Position = 0;
        var received = await SensorHostWire.ReadResponseAsync(stream, CancellationToken.None);
        Assert.Equal(61.5, received.TemperatureCelsius);
        Assert.Null(received.PackagePowerWatts);
        Assert.Equal(SensorHostState.CpuSensorsUnavailable, received.State);
        Assert.True(received.TemperatureSensorFound);
        Assert.True(received.PowerSensorFound);
    }

    [Fact]
    public async Task Protocol_RoundTripsHandshakeErrorAndShutdown()
    {
        using var stream = new MemoryStream();
        await SensorHostWire.WriteRequestAsync(stream,
            new SensorHostRequest(SensorHostWire.Version, SensorHostOperation.Hello), CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(SensorHostOperation.Hello, (await SensorHostWire.ReadRequestAsync(stream, CancellationToken.None)).Operation);
        stream.SetLength(0);
        stream.Position = 0;
        await SensorHostWire.WriteResponseAsync(stream,
            new SensorHostResponse(SensorHostWire.Version, SensorHostMessageKind.Error,
                SensorHostState.ProtocolMismatch, Detail: "version mismatch"), CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(SensorHostState.ProtocolMismatch,
            (await SensorHostWire.ReadResponseAsync(stream, CancellationToken.None)).State);
        stream.SetLength(0);
        stream.Position = 0;
        await SensorHostWire.WriteRequestAsync(stream,
            new SensorHostRequest(SensorHostWire.Version, SensorHostOperation.Shutdown), CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(SensorHostOperation.Shutdown, (await SensorHostWire.ReadRequestAsync(stream, CancellationToken.None)).Operation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task Protocol_RejectsInvalidLength(int length)
    {
        using var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        stream.Write(header);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => SensorHostWire.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Protocol_RejectsMalformedJsonAndDisconnect()
    {
        using var stream = new MemoryStream();
        byte[] payload = Encoding.UTF8.GetBytes("{not-json}");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => SensorHostWire.ReadRequestAsync(stream, CancellationToken.None));
        stream.SetLength(0);
        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => SensorHostWire.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void PipeIdentityAndLaunch_ArePerLaunchCurrentUserAndExplicitRunas()
    {
        string first = SensorHostClient.CreatePipeName();
        string second = SensorHostClient.CreatePipeName();
        Assert.NotEqual(first, second);
        Assert.StartsWith("dev-overlay-cpu-", first);
        Assert.True(SensorHostClient.PipeSecurityOptions.HasFlag(PipeOptions.CurrentUserOnly));
        string helper = SensorHostClient.HelperPath;
        Assert.EndsWith(Path.Combine("SensorHost", "DevOverlay.SensorHost.exe"), helper);
        var info = SensorHostClient.CreateLaunchInfo(helper, first, 1234, 5678);
        Assert.True(info.UseShellExecute);
        Assert.Equal("runas", info.Verb);
        Assert.Equal(helper, info.FileName);
        Assert.Equal([first, "1234", "5678"], info.ArgumentList);
        Assert.DoesNotContain(info.ArgumentList, argument => argument.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Client_NotRunningDoesNotLaunchAndReturnsUnavailable()
    {
        var launcher = new CancellingLauncher();
        await using var client = new SensorHostClient(launcher);
        Assert.Equal(SensorHostState.NotRunning, client.State);
        Assert.Equal(default, await client.ReadAsync(CancellationToken.None));
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public async Task ExplicitLaunch_UacCancellationIsNonfatalAndDoesNotConnect()
    {
        var helper = Path.Combine(Path.GetTempPath(), $"DevOverlay-SensorHost-test-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(helper, []);
            var launcher = new CancellingLauncher();
            await using var client = new SensorHostClient(launcher, helper);
            await client.StartAsync();
            Assert.Equal(1, launcher.Calls);
            Assert.Equal("runas", launcher.LastInfo?.Verb);
            Assert.Equal(SensorHostState.ElevationCancelled, client.State);
            Assert.False(client.IsConnected);
            Assert.Equal(default, await client.ReadAsync(CancellationToken.None));
        }
        finally { File.Delete(helper); }
    }

    [Fact]
    public async Task PipeHandshakeSnapshotDisconnectAndReconnect_WorkWithoutElevation()
    {
        var helper = Path.Combine(Path.GetTempPath(), $"DevOverlay-SensorHost-test-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(helper, []);
            var launcher = new FakePipeLauncher();
            await using var client = new SensorHostClient(launcher, helper);
            await client.StartAsync();
            Assert.Equal(SensorHostState.Ready, client.State);
            Assert.Equal(new CpuPackageReadings(64, 28), await client.ReadAsync(CancellationToken.None));
            Assert.Equal(default, await client.ReadAsync(CancellationToken.None));
            Assert.Equal(SensorHostState.Disconnected, client.State);
            await client.StartAsync();
            Assert.Equal(SensorHostState.Ready, client.State);
            Assert.Equal(new CpuPackageReadings(64, 28), await client.ReadAsync(CancellationToken.None));
            Assert.Equal(2, launcher.Calls);
        }
        finally { File.Delete(helper); }
    }

    [Fact]
    public async Task ProtocolMismatchAndConnectionTimeout_AreNonfatal()
    {
        var helper = Path.Combine(Path.GetTempPath(), $"DevOverlay-SensorHost-test-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(helper, []);
            await using (var mismatch = new SensorHostClient(new FakePipeLauncher(wrongVersion: true), helper))
            {
                await mismatch.StartAsync();
                Assert.Equal(SensorHostState.ProtocolMismatch, mismatch.State);
                Assert.Equal(default, await mismatch.ReadAsync(CancellationToken.None));
            }
            await using (var timeout = new SensorHostClient(new NonConnectingLauncher(), helper, TimeSpan.FromMilliseconds(100)))
            {
                await timeout.StartAsync();
                Assert.Equal(SensorHostState.Failed, timeout.State);
                Assert.Equal(default, await timeout.ReadAsync(CancellationToken.None));
            }
        }
        finally { File.Delete(helper); }
    }

    [Fact]
    public async Task Provider_OnlyConsumesSourcePackageValues_AndInvalidatesOnDisconnect()
    {
        var source = new FakeSource();
        await using var provider = new CpuPackageSensorMetricProvider(source);
        Assert.All(await provider.CollectAsync(CancellationToken.None), metric => Assert.False(metric.IsAvailable));
        source.Readings = new CpuPackageReadings(65, 25);
        var ready = await provider.CollectAsync(CancellationToken.None);
        Assert.Equal(65, ready.Single(metric => metric.Id == MetricId.CpuTemperature).Value);
        Assert.Equal(25, ready.Single(metric => metric.Id == MetricId.CpuPower).Value);
        source.Readings = default;
        Assert.All(await provider.CollectAsync(CancellationToken.None), metric => Assert.False(metric.IsAvailable));
        source.Readings = new CpuPackageReadings(58, 0);
        var partial = await provider.CollectAsync(CancellationToken.None);
        Assert.True(partial.Single(metric => metric.Id == MetricId.CpuTemperature).IsAvailable);
        Assert.False(partial.Single(metric => metric.Id == MetricId.CpuPower).IsAvailable);
        Assert.Equal(0, provider.ContextGeneration); // IPC source never opens a second in-process LHM context.
        Assert.Contains(App.CreateRuntimeProviders(OverlaySettings.CreateDefault(), source),
            item => item is CpuUtilizationMetricProvider);
    }

    [Fact]
    public void SensorHostStatus_DoesNotChangeCpuVisibilitySettings()
    {
        var settings = OverlaySettings.CreateDefault();
        var viewModel = new SettingsViewModel(settings);
        int changes = 0;
        viewModel.SettingsChanged += _ => changes++;
        viewModel.UpdateSensorHostStatus(SensorHostState.Ready);
        Assert.Contains("ready", viewModel.SensorHostStatusText);
        Assert.Equal(0, changes);
        Assert.Equal(settings.EnabledMetrics.Contains(MetricId.CpuTemperature), viewModel.CpuTemperatureEnabled);
    }

    private sealed class FakeSource : ICpuPackageSensorSource
    {
        public CpuPackageReadings Readings { get; set; }
        public Task<CpuPackageReadings> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Readings);
    }

    private sealed class CancellingLauncher : ISensorHostProcessLauncher
    {
        public int Calls { get; private set; }
        public ProcessStartInfo? LastInfo { get; private set; }
        public Process? Start(ProcessStartInfo info)
        {
            Calls++;
            LastInfo = info;
            throw new Win32Exception(1223);
        }
    }

    private sealed class NonConnectingLauncher : ISensorHostProcessLauncher
    {
        public Process Start(ProcessStartInfo info) => Process.GetCurrentProcess();
    }

    private sealed class FakePipeLauncher(bool wrongVersion = false) : ISensorHostProcessLauncher
    {
        public int Calls { get; private set; }
        public Process Start(ProcessStartInfo info)
        {
            Calls++;
            string pipeName = info.ArgumentList[0];
            _ = Task.Run(async () =>
            {
                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000);
                var hello = await SensorHostWire.ReadRequestAsync(pipe, CancellationToken.None);
                Assert.Equal(SensorHostOperation.Hello, hello.Operation);
                await SensorHostWire.WriteResponseAsync(pipe,
                    new SensorHostResponse(wrongVersion ? 999 : SensorHostWire.Version, SensorHostMessageKind.Hello,
                        SensorHostState.Ready), CancellationToken.None);
                if (wrongVersion) return;
                var snapshot = await SensorHostWire.ReadRequestAsync(pipe, CancellationToken.None);
                Assert.Equal(SensorHostOperation.GetCpuPackageSnapshot, snapshot.Operation);
                await SensorHostWire.WriteResponseAsync(pipe,
                    new SensorHostResponse(SensorHostWire.Version, SensorHostMessageKind.CpuPackageSnapshot,
                        SensorHostState.Ready, 64, 28, DateTimeOffset.UtcNow), CancellationToken.None);
            });
            return Process.GetCurrentProcess();
        }
    }
}
