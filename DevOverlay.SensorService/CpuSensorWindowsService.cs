using System.Diagnostics;
using System.ServiceProcess;
using DevOverlay.SensorHostProtocol;

namespace DevOverlay.SensorService;

internal sealed class CpuSensorWindowsService : ServiceBase
{
    private CancellationTokenSource? _lifetime;
    private ServicePipeServer? _server;
    private Task? _serverTask;

    public CpuSensorWindowsService()
    {
        ServiceName = SensorServiceIdentity.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        _lifetime = new CancellationTokenSource();
        _server = new ServicePipeServer();
        ServiceLog.Write("Service started; waiting for a local client.");
        _serverTask = RunServerAsync(_server, _lifetime.Token);
    }

    private async Task RunServerAsync(ServicePipeServer server, CancellationToken token)
    {
        try { await server.RunAsync(token); }
        catch (Exception exception)
        {
            ServiceLog.Write($"Service pipe failed: {exception}", EventLogEntryType.Error);
            ExitCode = 1;
            if (!token.IsCancellationRequested) _ = Task.Run(Stop);
        }
    }

    protected override void OnStop()
    {
        var lifetime = _lifetime;
        if (lifetime is null) return;
        _lifetime = null;
        ServiceLog.Write("Service stopping.");
        lifetime.Cancel();
        try { _serverTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException exception) { Trace.WriteLine(exception); }
        _server?.Dispose();
        _server = null;
        _serverTask = null;
        lifetime.Dispose();
    }

    protected override void OnShutdown() => OnStop();
}

internal static class ServiceLog
{
    public static void Write(string message, EventLogEntryType type = EventLogEntryType.Information)
    {
        try { EventLog.WriteEntry(SensorServiceIdentity.ServiceName, message, type); }
        catch (Exception exception)
        { Trace.WriteLine($"Service log unavailable: {exception.Message}; {message}"); }
    }
}
