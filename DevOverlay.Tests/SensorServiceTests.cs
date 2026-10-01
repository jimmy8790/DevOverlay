using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;
using DevOverlay.SensorHostProtocol;
using Xunit;

namespace DevOverlay.Tests;

public sealed class SensorServiceTests
{
    [Fact]
    public void ServiceIdentityAndPipeRightsAreNarrow()
    {
        Assert.Equal("DevOverlaySensorService", SensorServiceIdentity.ServiceName);
        Assert.Equal(1, SensorHostWire.Version); // Protocol remains explicitly versioned.
        Assert.Equal("LocalSystem", SensorServiceIdentity.ServiceAccount);
        Assert.Equal(2u, SensorServiceIdentity.AutomaticStartType);
        Assert.Equal("\"C:\\Program Files\\DevOverlay\\DevOverlay.SensorService.exe\"",
            SensorServiceIdentity.QuoteExecutablePath(@"C:\Program Files\DevOverlay\DevOverlay.SensorService.exe"));
        Assert.Equal(0, SensorServiceIdentity.PipeClientRights & 0x4); // no FILE_CREATE_PIPE_INSTANCE
        Assert.Equal(0x00100083, SensorServiceIdentity.PipeClientRights);
        Assert.Contains("0x00100083", SensorServiceIdentity.PipeSecurityDescriptor);
        Assert.Contains(";;;BU)", SensorServiceIdentity.PipeSecurityDescriptor);
        Assert.DoesNotContain(";;;WD)", SensorServiceIdentity.PipeSecurityDescriptor); // no Everyone ACE
        Assert.Equal(0x8u, SensorServiceIdentity.PipeRejectRemoteClients);
        Assert.Equal("dev-overlay-cpu-service-v1", SensorServiceIdentity.PipeName);
    }

    [Theory]
    [InlineData(0, "Not installed", true, false, false)]
    [InlineData(1, "Stopped", false, true, true)]
    [InlineData(2, "Starting", false, false, false)]
    [InlineData(3, "Running", false, true, true)]
    [InlineData(5, "Broken", false, true, true)]
    public void SettingsExposeServiceStateWithoutChangingVisibility(int stateValue, string label,
        bool install, bool repair, bool uninstall)
    {
        var settings = OverlaySettings.CreateDefault();
        var viewModel = new SettingsViewModel(settings);
        int changed = 0;
        viewModel.SettingsChanged += _ => changed++;
        viewModel.UpdateSensorServiceStatus(new((SensorServiceRunState)stateValue));
        Assert.Contains(label, viewModel.SensorServiceStatusText);
        Assert.Equal(install, viewModel.CanInstallSensorService);
        Assert.Equal(repair, viewModel.CanRepairSensorService);
        Assert.Equal(uninstall, viewModel.CanUninstallSensorService);
        viewModel.UpdateSensorServiceActionProgress(SensorServiceAction.Repair);
        Assert.False(viewModel.CanInstallSensorService);
        Assert.False(viewModel.CanRepairSensorService);
        Assert.False(viewModel.CanUninstallSensorService);
        viewModel.UpdateSensorServiceActionResult(new(false, true, "Administrator approval was cancelled."));
        Assert.Equal(repair, viewModel.CanRepairSensorService);
        viewModel.UpdateSensorServiceConnection(SensorHostState.ProtocolMismatch, "wrong version");
        Assert.Contains("Incompatible", viewModel.SensorServiceConnectionText);
        viewModel.UpdateSensorServiceConnection(SensorHostState.Ready, null);
        Assert.Contains("ready", viewModel.SensorServiceConnectionText);
        Assert.Equal(0, changed);
        Assert.Equal(settings.EnabledMetrics.Contains(MetricId.CpuTemperature), viewModel.CpuTemperatureEnabled);
    }

    [Theory]
    [InlineData(0, "--install")]
    [InlineData(1, "--repair")]
    [InlineData(2, "--uninstall")]
    public void InstallerCommandsRequireExplicitRunas(int actionValue, string command)
    {
        var path = @"C:\Program Files\DevOverlay\DevOverlay.SensorService.exe";
        var info = SensorServiceManagement.CreateLaunchInfo(path, (SensorServiceAction)actionValue);
        Assert.Equal(path, info.FileName);
        Assert.Equal([command], info.ArgumentList);
        Assert.True(info.UseShellExecute);
        Assert.Equal("runas", info.Verb);
    }

    [Fact]
    public async Task UacCancellationIsNonfatalAndNeverTouchesScmInTests()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DevOverlay-service-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(path, []);
            var launcher = new CancellingLauncher();
            var management = new SensorServiceManagement(launcher, path);
            var result = await management.ExecuteAsync(SensorServiceAction.Install);
            Assert.True(result.ElevationCancelled);
            Assert.False(result.Succeeded);
            Assert.Equal(1, launcher.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ClientReadsPackageValuesThenInvalidatesAndReconnects()
    {
        string name = $"dev-overlay-service-test-{Guid.NewGuid():N}";
        var status = new FakeStatusReader(new(SensorServiceRunState.Running, (uint)Environment.ProcessId));
        await using var client = new SensorServiceClient(status, name, TimeSpan.Zero);
        await using (var server = NewServer(name))
        {
            var serving = ServeOneSnapshot(server, 63, 18);
            Assert.Equal(new CpuPackageReadings(63, 18), await client.ReadAsync(CancellationToken.None));
            await serving;
        }
        Assert.Equal(default, await client.ReadAsync(CancellationToken.None));
        Assert.Equal(SensorHostState.Disconnected, client.State);
        await using (var server = NewServer(name))
        {
            var serving = ServeOneSnapshot(server, 65, 25);
            Assert.Equal(new CpuPackageReadings(65, 25), await client.ReadAsync(CancellationToken.None));
            await serving;
        }
        Assert.Equal(SensorHostState.Ready, client.State);
        Assert.True(status.Calls >= 2);
    }

    [Fact]
    public async Task IncompatibleServiceDoesNotExposeTelemetry()
    {
        string name = $"dev-overlay-service-test-{Guid.NewGuid():N}";
        await using var server = NewServer(name);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            Assert.Equal(SensorHostOperation.Hello, (await SensorHostWire.ReadRequestAsync(server, CancellationToken.None)).Operation);
            await SensorHostWire.WriteResponseAsync(server,
                new(999, SensorHostMessageKind.Hello, SensorHostState.ProtocolMismatch), CancellationToken.None);
        });
        await using var client = new SensorServiceClient(
            new FakeStatusReader(new(SensorServiceRunState.Running, (uint)Environment.ProcessId)), name, TimeSpan.Zero);
        Assert.Equal(default, await client.ReadAsync(CancellationToken.None));
        Assert.Equal(SensorHostState.ProtocolMismatch, client.State);
        await serving;
    }

    [Fact]
    public async Task ServiceAbsentNeverTriggersElevationAndCpuUsageProviderRemainsIndependent()
    {
        var status = new FakeStatusReader(new(SensorServiceRunState.NotInstalled));
        await using var client = new SensorServiceClient(status, $"missing-{Guid.NewGuid():N}", TimeSpan.Zero);
        await using var package = new CpuPackageSensorMetricProvider(client);
        Assert.All(await package.CollectAsync(CancellationToken.None), item => Assert.False(item.IsAvailable));
        Assert.Contains(App.CreateRuntimeProviders(OverlaySettings.CreateDefault(), client),
            provider => provider is CpuUtilizationMetricProvider);
        Assert.Equal(SensorHostState.NotRunning, client.State);
    }

    [Fact]
    public async Task RunningServiceWithUnavailablePipeFailsCleanly()
    {
        await using var client = new SensorServiceClient(
            new FakeStatusReader(new(SensorServiceRunState.Running, (uint)Environment.ProcessId)),
            $"missing-{Guid.NewGuid():N}", TimeSpan.Zero);
        Assert.Equal(default, await client.ReadAsync(CancellationToken.None));
        Assert.Equal(SensorHostState.Disconnected, client.State);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task StaleServiceSnapshotIsUnavailable()
    {
        string name = $"dev-overlay-service-test-{Guid.NewGuid():N}";
        await using var server = NewServer(name);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await SensorHostWire.ReadRequestAsync(server, CancellationToken.None);
            await SensorHostWire.WriteResponseAsync(server,
                new(SensorHostWire.Version, SensorHostMessageKind.Hello, SensorHostState.Ready), CancellationToken.None);
            await SensorHostWire.ReadRequestAsync(server, CancellationToken.None);
            await SensorHostWire.WriteResponseAsync(server,
                new(SensorHostWire.Version, SensorHostMessageKind.CpuPackageSnapshot, SensorHostState.Ready,
                    60, 20, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)), CancellationToken.None);
        });
        await using var client = new SensorServiceClient(
            new FakeStatusReader(new(SensorServiceRunState.Running, (uint)Environment.ProcessId)), name, TimeSpan.Zero);
        Assert.Equal(default, await client.ReadAsync(CancellationToken.None));
        Assert.Equal(SensorHostState.CpuSensorsUnavailable, client.State);
        await serving;
    }

    [Fact]
    public async Task BuiltServiceExecutableServesLocalPipeWithoutInstallingService()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string executable = Path.Combine(repository, "bin", configuration, "net8.0-windows10.0.19041.0",
            "SensorService", "DevOverlay.SensorService.exe");
        Assert.True(File.Exists(executable), $"Service build output missing: {executable}");
        string name = $"dev-overlay-service-test-{Guid.NewGuid():N}";
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--diagnostic-pipe");
        info.ArgumentList.Add(name);
        using var process = Process.Start(info);
        Assert.NotNull(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var client = new SensorServiceClient(
            new FakeStatusReader(new(SensorServiceRunState.Running, (uint)process.Id)), name, TimeSpan.Zero))
        {
            while (!client.IsConnected && !timeout.IsCancellationRequested)
            {
                await client.ReadAsync(timeout.Token);
                if (!client.IsConnected) await Task.Delay(100, timeout.Token);
            }
            Assert.True(client.IsConnected, $"Service pipe did not connect: {client.State}; {client.Detail}");
            Assert.NotEqual(SensorHostState.ProtocolMismatch, client.State);
        }
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task ServiceProtocolSupportsStatusButRejectsShutdown()
    {
        string executable = BuiltServiceExecutable();
        string name = $"dev-overlay-service-test-{Guid.NewGuid():N}";
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--diagnostic-pipe");
        info.ArgumentList.Add(name);
        using var process = Process.Start(info);
        Assert.NotNull(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        FileStream? pipe = null;
        int lastError = 0;
        while (pipe is null)
        {
            try { pipe = SensorServiceClient.OpenPipe(name); }
            catch (Win32Exception exception)
            {
                lastError = exception.NativeErrorCode;
                if (timeout.IsCancellationRequested) throw new IOException($"Service pipe open failed: Windows error {lastError}.");
                await Task.Delay(100);
            }
        }
        await using (pipe)
        {
            await SensorHostWire.WriteRequestAsync(pipe,
                new(SensorHostWire.Version, SensorHostOperation.Hello), timeout.Token);
            Assert.Equal(SensorHostMessageKind.Hello,
                (await SensorHostWire.ReadResponseAsync(pipe, timeout.Token)).Kind);
            await SensorHostWire.WriteRequestAsync(pipe,
                new(SensorHostWire.Version, SensorHostOperation.GetStatus), timeout.Token);
            var status = await SensorHostWire.ReadResponseAsync(pipe, timeout.Token);
            Assert.Equal(SensorHostMessageKind.Status, status.Kind);
            await SensorHostWire.WriteRequestAsync(pipe,
                new(SensorHostWire.Version, SensorHostOperation.Shutdown), timeout.Token);
            var denied = await SensorHostWire.ReadResponseAsync(pipe, timeout.Token);
            Assert.Equal(SensorHostMessageKind.Error, denied.Kind);
            Assert.Contains("Unsupported", denied.Detail);
        }
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task ServiceProtocolRejectsVersionMismatch()
    {
        string name = $"dev-overlay-service-test-{Guid.NewGuid():N}";
        var info = new ProcessStartInfo(BuiltServiceExecutable()) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--diagnostic-pipe");
        info.ArgumentList.Add(name);
        using var process = Process.Start(info);
        Assert.NotNull(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        FileStream? pipe = null;
        int lastError = 0;
        while (pipe is null)
        {
            try { pipe = SensorServiceClient.OpenPipe(name); }
            catch (Win32Exception exception)
            {
                lastError = exception.NativeErrorCode;
                if (timeout.IsCancellationRequested) throw new IOException($"Service pipe open failed: Windows error {lastError}.");
                await Task.Delay(100);
            }
        }
        await using (pipe)
        {
            await SensorHostWire.WriteRequestAsync(pipe,
                new(SensorHostWire.Version + 1, SensorHostOperation.Hello), timeout.Token);
            var response = await SensorHostWire.ReadResponseAsync(pipe, timeout.Token);
            Assert.Equal(SensorHostMessageKind.Error, response.Kind);
            Assert.Equal(SensorHostState.ProtocolMismatch, response.State);
        }
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
    }

    private static string BuiltServiceExecutable()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string executable = Path.Combine(repository, "bin", configuration, "net8.0-windows10.0.19041.0",
            "SensorService", "DevOverlay.SensorService.exe");
        Assert.True(File.Exists(executable), $"Service build output missing: {executable}");
        return executable;
    }

    private static NamedPipeServerStream NewServer(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task ServeOneSnapshot(NamedPipeServerStream server, double temperature, double power)
    {
        await server.WaitForConnectionAsync();
        Assert.Equal(SensorHostOperation.Hello, (await SensorHostWire.ReadRequestAsync(server, CancellationToken.None)).Operation);
        await SensorHostWire.WriteResponseAsync(server,
            new(SensorHostWire.Version, SensorHostMessageKind.Hello, SensorHostState.Ready), CancellationToken.None);
        Assert.Equal(SensorHostOperation.GetCpuPackageSnapshot,
            (await SensorHostWire.ReadRequestAsync(server, CancellationToken.None)).Operation);
        await SensorHostWire.WriteResponseAsync(server,
            new(SensorHostWire.Version, SensorHostMessageKind.CpuPackageSnapshot, SensorHostState.Ready,
                temperature, power, DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private sealed class FakeStatusReader(SensorServiceStatus status) : ISensorServiceStatusReader
    {
        public int Calls { get; private set; }
        public SensorServiceStatus Read() { Calls++; return status; }
    }

    private sealed class CancellingLauncher : ISensorServiceInstallerLauncher
    {
        public int Calls { get; private set; }
        public Process? Start(ProcessStartInfo info)
        {
            Calls++;
            throw new Win32Exception(1223);
        }
    }
}
