using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using DevOverlay.Metrics.Windows;
using DevOverlay.SensorHostProtocol;
using Microsoft.Win32.SafeHandles;

namespace DevOverlay.Platform.Windows;

internal interface ISensorHostProcessLauncher
{
    Process? Start(ProcessStartInfo info);
}

internal sealed class SensorHostProcessLauncher : ISensorHostProcessLauncher
{
    public Process? Start(ProcessStartInfo info) => Process.Start(info);
}

/// <summary>The non-elevated app owns a one-launch, current-user pipe; only CPU package snapshots cross it.</summary>
internal sealed class SensorHostClient(ISensorHostProcessLauncher? launcher = null, string? helperPath = null,
    TimeSpan? connectionTimeout = null) : ICpuPackageSensorSource, IAsyncDisposable
{
    private readonly ISensorHostProcessLauncher _launcher = launcher ?? new SensorHostProcessLauncher();
    private readonly string _helperPath = helperPath ?? HelperPath;
    private readonly TimeSpan _connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private SensorHostState _state = SensorHostState.NotRunning;
    private bool _disposed;

    public event Action<SensorHostState, string?>? StateChanged;
    public SensorHostState State => _state;
    public bool IsConnected => _pipe?.IsConnected == true;
    public string? Detail { get; private set; }

    internal static string CreatePipeName() => $"dev-overlay-cpu-{Guid.NewGuid():N}";
    internal static PipeOptions PipeSecurityOptions => PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    internal static string HelperPath => Path.Combine(AppContext.BaseDirectory, "SensorHost", "DevOverlay.SensorHost.exe");
    internal static ProcessStartInfo CreateLaunchInfo(string helperPath, string pipeName, int parentPid, long parentStartTicks)
    {
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(helperPath)! };
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add(parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(parentStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return info;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is SensorHostState.Starting or SensorHostState.Connecting or SensorHostState.Ready) return;
            await _requestGate.WaitAsync(cancellationToken);
            try { await CloseConnectionAsync(sendShutdown: IsConnected); }
            finally { _requestGate.Release(); }
            if (!File.Exists(_helperPath)) { SetState(SensorHostState.Failed, "SensorHost executable is missing."); return; }

            string pipeName = CreatePipeName();
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeSecurityOptions);
            using var parent = Process.GetCurrentProcess();
            var info = CreateLaunchInfo(_helperPath, pipeName, parent.Id, parent.StartTime.ToUniversalTime().Ticks);
            SetState(SensorHostState.Starting);
            // ShellExecute/runas can wait for UAC input; never block the WPF dispatcher on it.
            try { _process = await Task.Run(() => _launcher.Start(info)); }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            { await CloseConnectionAsync(false); SetState(SensorHostState.ElevationCancelled); return; }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            { await CloseConnectionAsync(false); SetState(SensorHostState.Failed, exception.Message); return; }
            if (_process is null) { await CloseConnectionAsync(false); SetState(SensorHostState.Failed, "SensorHost did not start."); return; }
            var launchedProcess = _process;
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                if (!ReferenceEquals(_process, launchedProcess)) return;
                _pipe?.Dispose();
                _pipe = null;
                SetState(SensorHostState.Disconnected, "SensorHost process exited.");
            };
            Debug.WriteLine($"SensorHost started: PID {_process.Id}.");
            SetState(SensorHostState.Connecting);
            var pendingPipe = _pipe;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectionTimeout);
            try
            {
                await pendingPipe.WaitForConnectionAsync(timeout.Token);
                if (!GetNamedPipeClientProcessId(pendingPipe.SafePipeHandle, out uint connectedPid) || connectedPid != launchedProcess.Id)
                    throw new IOException("Unexpected SensorHost pipe client process.");
                var hello = await ExchangeAsync(new SensorHostRequest(SensorHostWire.Version, SensorHostOperation.Hello), timeout.Token);
                if (hello.Version != SensorHostWire.Version || hello.State == SensorHostState.ProtocolMismatch)
                    throw new SensorHostProtocolException("Incompatible SensorHost protocol version.");
                if (hello.Kind != SensorHostMessageKind.Hello)
                    throw new InvalidDataException("Invalid SensorHost handshake.");
                SetState(launchedProcess.HasExited ? SensorHostState.Disconnected : hello.State, hello.Detail);
                Debug.WriteLine($"SensorHost handshake completed: {hello.State}.");
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SensorHostProtocolException)
            {
                await CloseConnectionAsync(false);
                SetState(exception is SensorHostProtocolException ? SensorHostState.ProtocolMismatch :
                    _state == SensorHostState.Disconnected ? SensorHostState.Disconnected : SensorHostState.Failed,
                    exception is OperationCanceledException ? "SensorHost connection timed out or was cancelled." : exception.Message);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            await CloseConnectionAsync(false);
            SetState(SensorHostState.Failed, exception.Message);
        }
        finally { _startGate.Release(); }
    }

    public async Task<CpuPackageReadings> ReadAsync(CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            if (_pipe?.IsConnected != true)
            {
                if (_state is SensorHostState.Connected or SensorHostState.Ready or SensorHostState.CpuSensorsUnavailable or
                    SensorHostState.PawnIoUnavailable or SensorHostState.PawnIoAccessDenied or SensorHostState.CpuHardwareUnavailable)
                    SetState(SensorHostState.Disconnected);
                return default;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var response = await ExchangeAsync(new SensorHostRequest(SensorHostWire.Version, SensorHostOperation.GetCpuPackageSnapshot), timeout.Token);
                if (response.Version != SensorHostWire.Version) throw new SensorHostProtocolException("Incompatible SensorHost protocol version.");
                if (response.Kind != SensorHostMessageKind.CpuPackageSnapshot) throw new InvalidDataException("Invalid SensorHost snapshot response.");
                SetState(response.State, response.Detail);
                return new CpuPackageReadings(response.TemperatureCelsius, response.PackagePowerWatts);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SensorHostProtocolException)
            {
                await CloseConnectionAsync(false);
                SetState(exception is SensorHostProtocolException ? SensorHostState.ProtocolMismatch : SensorHostState.Disconnected,
                    exception.Message);
                return default;
            }
        }
        finally { _requestGate.Release(); }
    }

    private async Task<SensorHostResponse> ExchangeAsync(SensorHostRequest request, CancellationToken token)
    {
        var pipe = _pipe ?? throw new IOException("SensorHost pipe is closed.");
        await SensorHostWire.WriteRequestAsync(pipe, request, token);
        return await SensorHostWire.ReadResponseAsync(pipe, token);
    }

    private async Task CloseConnectionAsync(bool sendShutdown)
    {
        if (sendShutdown && _pipe?.IsConnected == true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await ExchangeAsync(new SensorHostRequest(SensorHostWire.Version, SensorHostOperation.Shutdown), timeout.Token); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { Debug.WriteLine($"SensorHost shutdown: {exception.Message}"); }
        }
        _pipe?.Dispose();
        _pipe = null;
        _process?.Dispose();
        _process = null;
    }

    private void SetState(SensorHostState state, string? detail = null)
    {
        if (_state == state && Detail == detail) return;
        _state = state;
        Detail = detail;
        StateChanged?.Invoke(state, detail);
        Debug.WriteLine($"SensorHost: {state}: {detail}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _startGate.WaitAsync();
        await _requestGate.WaitAsync();
        try { await CloseConnectionAsync(true); SetState(SensorHostState.NotRunning); }
        finally { _requestGate.Release(); _startGate.Release(); _requestGate.Dispose(); _startGate.Dispose(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

internal sealed class SensorHostProtocolException(string message) : Exception(message);
