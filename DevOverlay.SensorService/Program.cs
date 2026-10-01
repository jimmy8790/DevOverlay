using System.Diagnostics;
using System.ServiceProcess;
using DevOverlay.SensorHostProtocol;

namespace DevOverlay.SensorService;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--diagnostic-pipe" &&
            args[1].StartsWith("dev-overlay-service-test-", StringComparison.Ordinal))
        {
            using var server = new ServicePipeServer(args[1]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            server.ServeSingleClientAsync(timeout.Token).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Length == 1 && args[0] is "--install" or "--repair" or "--uninstall")
        {
            try
            {
                ServiceInstaller.Execute(args[0]);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                ReportSetupFailure(args[0], exception);
                return 1;
            }
        }
        if (args.Length != 0) return 2;
        ServiceBase.Run(new CpuSensorWindowsService());
        return 0;
    }

    // The installer runs elevated in a hidden window; the Application event log is where Settings points users on failure.
    private static void ReportSetupFailure(string action, Exception exception)
    {
        try
        {
            if (!EventLog.SourceExists(SensorServiceIdentity.ServiceName))
                EventLog.CreateEventSource(SensorServiceIdentity.ServiceName, "Application");
            EventLog.WriteEntry(SensorServiceIdentity.ServiceName, $"Sensor Service {action} failed: {exception}", EventLogEntryType.Error);
        }
        catch (Exception logFailure) when (logFailure is InvalidOperationException or System.Security.SecurityException or
            ArgumentException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(logFailure);
        }
    }
}
