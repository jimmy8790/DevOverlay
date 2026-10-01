using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using DevOverlay.Metrics.Windows;
using DevOverlay.SensorHostProtocol;
using Microsoft.Win32.SafeHandles;

namespace DevOverlay.Platform.Windows;

/// <summary>Unprivileged automatic client; only CPU Package snapshots are accepted from the SCM service PID.</summary>
internal sealed class SensorServiceClient(ISensorServiceStatusReader? statusReader = null,
    string? pipeName = null, TimeSpan? retryDelay = null) : ICpuPackageSensorSource, IAsyncDisposable
{
    private readonly ISensorServiceStatusReader _statusReader = statusReader ?? new SensorServiceStatusReader();
    private readonly string _pipeName = pipeName ?? SensorServiceIdentity.PipeName;
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _pipe;
    private DateTimeOffset _nextConnectAt;
    private SensorHostState _state = SensorHostState.NotRunning;
    private bool _disposed;

    public event Action<SensorHostState, string?>? StateChanged;
    public SensorHostState State => _state;
    public string? Detail { get; private set; }
    public bool IsConnected => _pipe is not null;

    public async Task<CpuPackageReadings> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return default;
            if (_pipe is null && !await ConnectIfDueAsync(cancellationToken).ConfigureAwait(false)) return default;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await SensorHostWire.WriteRequestAsync(_pipe!,
                    new(SensorHostWire.Version, SensorHostOperation.GetCpuPackageSnapshot), timeout.Token).ConfigureAwait(false);
                var response = await SensorHostWire.ReadResponseAsync(_pipe!, timeout.Token).ConfigureAwait(false);
                if (response.Version != SensorHostWire.Version || response.State == SensorHostState.ProtocolMismatch)
                {
                    Disconnect(SensorHostState.ProtocolMismatch, "Incompatible Sensor Service protocol version.");
                    return default;
                }
                if (response.Kind != SensorHostMessageKind.CpuPackageSnapshot)
                    throw new InvalidDataException("Unexpected Sensor Service response.");
                if (response.TimestampUtc is not { } updated ||
                    updated < DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5) ||
                    updated > DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5))
                {
                    SetState(SensorHostState.CpuSensorsUnavailable, "Sensor Service snapshot is stale.");
                    return default;
                }
                SetState(response.State, response.Detail);
                return new(response.TemperatureCelsius, response.PackagePowerWatts);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                Disconnect(SensorHostState.Disconnected, exception.Message);
                return default;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> ConnectIfDueAsync(CancellationToken token)
    {
        if (DateTimeOffset.UtcNow < _nextConnectAt) return false;
        _nextConnectAt = DateTimeOffset.UtcNow + _retryDelay;
        var service = _statusReader.Read();
        if (service.State != SensorServiceRunState.Running || service.ProcessId == 0)
        {
            SetState(SensorHostState.NotRunning, service.Detail ?? $"Sensor Service: {service.State}.");
            return false;
        }
        SetState(SensorHostState.Connecting);
        FileStream pipe;
        try { pipe = OpenPipe(_pipeName); }
        catch (Win32Exception exception)
        {
            SetState(SensorHostState.Disconnected, $"Sensor Service pipe unavailable: Windows error {exception.NativeErrorCode}.");
            return false;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            if (!GetNamedPipeServerProcessId(pipe.SafeFileHandle, out uint pid) || pid != service.ProcessId)
                throw new IOException("Unexpected Sensor Service pipe server process.");
            await SensorHostWire.WriteRequestAsync(pipe,
                new(SensorHostWire.Version, SensorHostOperation.Hello), timeout.Token).ConfigureAwait(false);
            var hello = await SensorHostWire.ReadResponseAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (hello.Version != SensorHostWire.Version || hello.State == SensorHostState.ProtocolMismatch)
            {
                _nextConnectAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                SetState(SensorHostState.ProtocolMismatch, "Incompatible Sensor Service protocol version.");
                return false;
            }
            if (hello.Kind != SensorHostMessageKind.Hello) throw new InvalidDataException("Invalid Sensor Service handshake.");
            _pipe = pipe;
            SetState(hello.State, hello.Detail);
            return true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException or Win32Exception)
        {
            SetState(SensorHostState.Disconnected, exception.Message);
            return false;
        }
        finally { if (!ReferenceEquals(_pipe, pipe)) pipe.Dispose(); }
    }

    internal static FileStream OpenPipe(string name)
    {
        var handle = CreateFile($@"\\.\pipe\{name}",
            SensorServiceIdentity.PipeClientRights, 0, nint.Zero, 3, 0x40000000, nint.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);
    }

    private void Disconnect(SensorHostState state, string? detail)
    {
        _pipe?.Dispose();
        _pipe = null;
        _nextConnectAt = DateTimeOffset.UtcNow + (state == SensorHostState.ProtocolMismatch
            ? TimeSpan.FromSeconds(30) : _retryDelay);
        SetState(state, detail);
    }

    private void SetState(SensorHostState state, string? detail = null)
    {
        if (_state == state && Detail == detail) return;
        _state = state;
        Detail = detail;
        StateChanged?.Invoke(state, detail);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _pipe?.Dispose();
            _pipe = null;
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafeFileHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, int desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);
}
