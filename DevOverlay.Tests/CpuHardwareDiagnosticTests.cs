using System.Runtime.InteropServices;
using DevOverlay.Platform.Windows;
using DevOverlay.Metrics.Windows;
using LibreHardwareMonitor.Hardware;
using Xunit;
using Xunit.Abstractions;

namespace DevOverlay.Tests;

public sealed class CpuHardwareDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InstalledPawnIoAndCpuSensors_AreReportedOnlyWhenExplicitlyRequested()
    {
        if (Environment.GetEnvironmentVariable("DEVOVERLAY_CPU_DIAGNOSTIC") != "1") return;

        output.WriteLine($"OS={RuntimeInformation.OSArchitecture}, process={RuntimeInformation.ProcessArchitecture}");
        var access = PawnIoDeviceAccessProbe.Probe();
        output.WriteLine($"PawnIO device accessible={access.IsAccessible}, Win32Error={access.Win32Error}");
        var computer = new Computer { IsCpuEnabled = true };
        computer.Open();
        try
        {
            foreach (var hardware in computer.Hardware.Where(hardware => hardware.HardwareType == HardwareType.Cpu))
            {
                output.WriteLine($"CPU={hardware.Name}, type={hardware.HardwareType}, id={hardware.Identifier}");
                for (var sample = 0; sample < 3; sample++)
                {
                    hardware.Update();
                    foreach (var sensor in hardware.Sensors.Where(sensor => sensor.SensorType is SensorType.Temperature or SensorType.Power))
                        output.WriteLine($"sample={sample}, type={sensor.SensorType}, name={sensor.Name}, id={sensor.Identifier}, value={sensor.Value?.ToString() ?? "null"}");
                    if (sample < 2) await Task.Delay(1000);
                }
            }
        }
        finally { computer.Close(); }

        await using var provider = new CpuPackageSensorMetricProvider();
        await provider.CollectAsync(CancellationToken.None);
        var originalGeneration = provider.ContextGeneration;
        provider.RequestReinitialize();
        var retried = await provider.CollectAsync(CancellationToken.None);
        output.WriteLine($"CPU provider context generation: {originalGeneration} -> {provider.ContextGeneration}");
        Assert.True(provider.ContextGeneration > originalGeneration);
        if (!access.IsAccessible) Assert.All(retried, metric => Assert.False(metric.IsAvailable));
    }
}
