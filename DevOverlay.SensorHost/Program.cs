using System.Diagnostics;
using System.IO.Pipes;
using DevOverlay.SensorHostProtocol;
using DevOverlay.SensorHostCore;

namespace DevOverlay.SensorHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Metadata only: the parent-created current-user pipe is the IPC security boundary.
        if (args.Length != 3 || !args[0].StartsWith("dev-overlay-cpu-", StringComparison.Ordinal) ||
            !int.TryParse(args[1], out int parentPid) || !long.TryParse(args[2], out long parentStartTicks)) return 2;
        if (!ParentIsOriginal(parentPid, parentStartTicks)) return 3;

        using var lifetime = new CancellationTokenSource();
        _ = WatchParentAsync(parentPid, parentStartTicks, lifetime);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(connectTimeout.Token);
            Trace.WriteLine("SensorHost: connected to parent pipe.");
            using var reader = new CpuPackageReader();
            SensorHostResponse status = reader.Initialize();
            while (pipe.IsConnected && !lifetime.IsCancellationRequested)
            {
                SensorHostRequest request;
                try { request = await SensorHostWire.ReadRequestAsync(pipe, lifetime.Token); }
                catch (InvalidDataException exception)
                {
                    await SensorHostWire.WriteResponseAsync(pipe,
                        new SensorHostResponse(SensorHostWire.Version, SensorHostMessageKind.Error, SensorHostState.Failed,
                            Detail: exception.Message), lifetime.Token);
                    break;
                }
                if (request.Version != SensorHostWire.Version)
                {
                    await SensorHostWire.WriteResponseAsync(pipe,
                        new SensorHostResponse(SensorHostWire.Version, SensorHostMessageKind.Error, SensorHostState.ProtocolMismatch,
                            Detail: "Incompatible SensorHost protocol version."), lifetime.Token);
                    break;
                }
                var response = request.Operation switch
                {
                    SensorHostOperation.Hello => status with { Kind = SensorHostMessageKind.Hello },
                    SensorHostOperation.GetCpuPackageSnapshot => status = reader.Read(),
                    SensorHostOperation.Shutdown => new SensorHostResponse(SensorHostWire.Version, SensorHostMessageKind.Goodbye, SensorHostState.Disconnected),
                    _ => new SensorHostResponse(SensorHostWire.Version, SensorHostMessageKind.Error, SensorHostState.Failed, Detail: "Unsupported operation.")
                };
                await SensorHostWire.WriteResponseAsync(pipe, response, lifetime.Token);
                if (request.Operation == SensorHostOperation.Shutdown || response.Kind == SensorHostMessageKind.Error) break;
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"SensorHost disconnected: {exception}");
            return 4;
        }
        finally { lifetime.Cancel(); }
    }

    private static bool ParentIsOriginal(int pid, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static async Task WatchParentAsync(int pid, long startTicks, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                if (!ParentIsOriginal(pid, startTicks)) { lifetime.Cancel(); break; }
            }
        }
        catch (OperationCanceledException) { }
    }
}
