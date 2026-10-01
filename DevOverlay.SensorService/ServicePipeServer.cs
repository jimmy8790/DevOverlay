using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using DevOverlay.SensorHostCore;
using DevOverlay.SensorHostProtocol;
using Microsoft.Win32.SafeHandles;

namespace DevOverlay.SensorService;

/// <summary>One local read-only telemetry client at a time; no service-control operation crosses this pipe.</summary>
internal sealed class ServicePipeServer : IDisposable
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private CpuPackageReader? _reader;
    private SensorHostResponse _last = new(SensorHostWire.Version, SensorHostMessageKind.Status, SensorHostState.NotRunning);
    private DateTimeOffset _initializedAt;
    private readonly string _pipeName;

    public ServicePipeServer(string? pipeName = null) => _pipeName = pipeName ?? SensorServiceIdentity.PipeName;

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await ServeSingleClientAsync(token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task ServeSingleClientAsync(CancellationToken token)
    {
        await using var pipe = CreatePipe();
        await pipe.WaitForConnectionAsync(token);
        ServiceLog.Write("IPC client connected.");
        try { await ServeClientAsync(pipe, token); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        { ServiceLog.Write($"IPC client disconnected: {exception.GetType().Name}."); }
        finally
        {
            _reader?.Dispose();
            _reader = null;
            _last = new(SensorHostWire.Version, SensorHostMessageKind.Status, SensorHostState.NotRunning);
            ServiceLog.Write("IPC client disconnected.");
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        while (pipe.IsConnected && !token.IsCancellationRequested)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            SensorHostRequest request;
            try { request = await SensorHostWire.ReadRequestAsync(pipe, requestTimeout.Token); }
            catch (InvalidDataException)
            {
                await SensorHostWire.WriteResponseAsync(pipe,
                    new(SensorHostWire.Version, SensorHostMessageKind.Error, SensorHostState.Failed,
                        Detail: "Malformed request."), requestTimeout.Token);
                break;
            }
            if (request.Version != SensorHostWire.Version)
            {
                ServiceLog.Write("Protocol mismatch.");
                await SensorHostWire.WriteResponseAsync(pipe,
                    new(SensorHostWire.Version, SensorHostMessageKind.Error, SensorHostState.ProtocolMismatch), requestTimeout.Token);
                break;
            }
            SensorHostResponse response;
            switch (request.Operation)
            {
                case SensorHostOperation.Hello:
                    EnsureReader();
                    response = _last with { Kind = SensorHostMessageKind.Hello };
                    break;
                case SensorHostOperation.GetStatus:
                    response = _last with { Kind = SensorHostMessageKind.Status };
                    break;
                case SensorHostOperation.GetCpuPackageSnapshot:
                    EnsureReader();
                    response = _reader!.Read();
                    if (response.State != _last.State ||
                        response.TemperatureSensorFound != _last.TemperatureSensorFound ||
                        response.PowerSensorFound != _last.PowerSensorFound)
                        LogBackend(response);
                    _last = response;
                    break;
                default:
                    // In particular, the prototype's Shutdown operation cannot stop the SCM service.
                    response = new(SensorHostWire.Version, SensorHostMessageKind.Error, SensorHostState.Failed,
                        Detail: "Unsupported service operation.");
                    break;
            }
            await SensorHostWire.WriteResponseAsync(pipe, response, requestTimeout.Token);
            if (response.Kind == SensorHostMessageKind.Error) break;
        }
    }

    private void EnsureReader()
    {
        if (_reader is not null &&
            (_last.State == SensorHostState.Ready || DateTimeOffset.UtcNow - _initializedAt < TimeSpan.FromSeconds(10))) return;
        var previous = _last;
        _reader?.Dispose();
        _reader = new CpuPackageReader();
        _initializedAt = DateTimeOffset.UtcNow;
        _last = _reader.Initialize();
        if (previous.State != _last.State || previous.TemperatureSensorFound != _last.TemperatureSensorFound ||
            previous.PowerSensorFound != _last.PowerSensorFound || previous.State == SensorHostState.NotRunning)
            LogBackend(_last);
    }

    private static void LogBackend(SensorHostResponse response) =>
        ServiceLog.Write($"CPU backend: {response.State}; package temperature sensor={response.TemperatureSensorFound}; " +
            $"package power sensor={response.PowerSensorFound}; {response.Detail}");

    private NamedPipeServerStream CreatePipe()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(SensorServiceIdentity.PipeSecurityDescriptor, 1, out nint descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create service pipe ACL.");
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor
            };
            var handle = CreateNamedPipe($@"\\.\pipe\{_pipeName}",
                PipeAccessDuplex | FileFlagOverlapped | FileFlagFirstPipeInstance,
                SensorServiceIdentity.PipeRejectRemoteClients, 1, SensorHostWire.MaxMessageBytes, SensorHostWire.MaxMessageBytes,
                0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Could not create service pipe.");
            }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }

    public void Dispose() => _reader?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string sddl, uint revision, out nint securityDescriptor, out uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint pointer);
}
