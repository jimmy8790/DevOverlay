using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using Windows.Devices.Enumeration;
using Windows.Devices.Power;
using Windows.System.Power;

// 모든 후보는 같은 프로세스에서 직렬 조회한다. 원시 값은 변환값과 별도로 보존한다.
if (args.Length == 2 && args[0] == "--replay") { BatteryTraceReplay.Run(args[1]); return; }
var duration = args.Length > 0 && int.TryParse(args[0], out var value) ? value : 360;
if (duration is < 5 or > 1800) throw new ArgumentOutOfRangeException(nameof(duration));
var logPath = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(AppContext.BaseDirectory, "battery-power-comparison.log");
using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
using var source = new SystemBatterySource();
using var identity = WindowsIdentity.GetCurrent();
var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
Console.WriteLine($"배터리 다중 소스 진단 시작. 관리자: {elevated}. 기록: {logPath}");
log.WriteLine(JsonSerializer.Serialize(new { Kind = "Metadata", Started = DateTimeOffset.Now, Elevated = elevated,
    SystemStateSize = Marshal.SizeOf<SystemState>(), Sdk = typeof(Battery).Assembly.FullName,
    RateContract = typeof(BatteryReport).GetProperty(nameof(BatteryReport.ChargeRateInMilliwatts))!.PropertyType.FullName }));
Battery? aggregate = null;
var individual = new List<Battery>();
string? winRtInitializationError = null;
try
{
    aggregate = Battery.AggregateBattery;
    foreach (var device in await DeviceInformation.FindAllAsync(Battery.GetDeviceSelector()))
        individual.Add(await Battery.FromIdAsync(device.Id));
}
catch (Exception exception) { winRtInitializationError = $"0x{exception.HResult:X8}: {exception.Message}"; }
var watch = Stopwatch.StartNew();
byte? previousAc = null;
double? transitionAt = null;
double lastPrint = -10;
string? previousAvailability = null;
var firstRates = new HashSet<string>();
var sawUnknown = new HashSet<string>();
var recoveredRates = new HashSet<string>();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
while (watch.Elapsed.TotalSeconds < duration && !cancellation.IsCancellationRequested)
{
    var sampleWatch = Stopwatch.StartNew();
    var reading = source.Read();
    var raw = source.LastNativeReading;
    var nativeReading = SystemBatteryInterpreter.Interpret(raw);
    var ac = raw.PowerStatus?.AcLineStatus;
    var transition = previousAc is <= 1 && ac is <= 1 && ac != previousAc;
    if (transition)
    { transitionAt = watch.Elapsed.TotalSeconds; firstRates.Clear(); sawUnknown.Clear(); recoveredRates.Clear(); }
    var ioctl = raw.Devices.Select(device => new { device.Capabilities, device.FullCapacity,
        QuerySucceeded = device.Status.HasValue, device.Status }).ToArray();
    // 태그는 진단에서만 reflection으로 읽어 제품 계약에 필드를 추가하지 않는다.
    var tags = ReadCachedTags(source);
    var result = NativePower.CallNtPowerInformation(5, IntPtr.Zero, 0, out var state, (uint)Marshal.SizeOf<SystemState>());
    var rate = unchecked((int)state.Rate);
    var systemWatts = result == 0 && state.BatteryPresent != 0
        ? ValidWatts(rate, state.Charging != 0, state.Discharging != 0, ac) : null;
    var aggregateReport = ReadReport(aggregate, ac, winRtInitializationError);
    var individualReports = individual.Select(battery => ReadReport(battery, ac, null)).ToArray();
    var now = watch.Elapsed.TotalSeconds;
    var firstValid = new Dictionary<string, double>();
    var recoveredAfterUnknown = new Dictionary<string, double>();
    foreach (var candidate in new[] { ("IOCTL", nativeReading.Watts), ("SystemBatteryState", systemWatts), ("WinRT", aggregateReport.Watts) })
    {
        if (!candidate.Item2.HasValue && transitionAt.HasValue) sawUnknown.Add(candidate.Item1);
        if (candidate.Item2.HasValue && firstRates.Add(candidate.Item1) && transitionAt.HasValue)
            firstValid[candidate.Item1] = Math.Round(now - transitionAt.Value, 3);
        if (candidate.Item2.HasValue && sawUnknown.Contains(candidate.Item1) && recoveredRates.Add(candidate.Item1) && transitionAt.HasValue)
            recoveredAfterUnknown[candidate.Item1] = Math.Round(now - transitionAt.Value, 3);
    }
    var row = new { Kind = "Sample", Time = DateTimeOffset.Now, ElapsedSeconds = Math.Round(now, 3), AC = ac,
        reading.Percentage, Flow = reading.Flow.ToString(), Transition = transition,
        SinceTransitionSeconds = transitionAt.HasValue ? Math.Round(now - transitionAt.Value, 3) : (double?)null,
        FirstValidSeconds = firstValid,
        FirstRecoveryAfterUnknownSeconds = recoveredAfterUnknown,
        IOCTL = new { Devices = ioctl, Tags = tags, Win32Errors = source.LastDeviceErrors, reading.Watts,
            PowerSource = reading.PowerSource.ToString(), Estimate = source.LastEstimate,
            NativeWatts = nativeReading.Watts,
            AbsoluteError = nativeReading.Watts.HasValue && source.LastEstimate.Watts.HasValue ? Math.Abs(nativeReading.Watts.Value - source.LastEstimate.Watts.Value) : (double?)null,
            PercentageError = nativeReading.Watts is not null and not 0 && source.LastEstimate.Watts.HasValue ? Math.Abs(nativeReading.Watts.Value - source.LastEstimate.Watts.Value) / Math.Abs(nativeReading.Watts.Value) * 100 : (double?)null,
            reading.RemainingSeconds, LeftSource = reading.LeftSource.ToString(),
            WindowsLifeSeconds = raw.PowerStatus?.LifeSeconds, reading.TimeToFullSeconds,
            FullSource = reading.FullSource.ToString(), NativeTimeToFullSeconds = nativeReading.TimeToFullSeconds,
            // 진단 전용 shadow: 같은 단일 장치에서만 Estimated W로 FULL을 계산해 Native FULL과 비교한다. 표시값과 섞지 않는다.
            ShadowEstimatedFullSeconds = reading.Flow == BatteryFlow.Charging && raw.Devices.Count == 1 &&
                source.LastEstimate is { Watts: double estimatedWatts and > 0, EndCapacity: { } estimatedCapacity }
                ? SystemBatteryInterpreter.DeriveFull(estimatedCapacity, raw.Devices[0].FullCapacity, estimatedWatts) : null },
        SystemBatteryState = new { QuerySucceeded = result == 0, NtStatus = $"0x{result:X8}",
            Raw = result == 0 ? state : (SystemState?)null, SignedRate = result == 0 ? rate : (int?)null, Watts = systemWatts },
        WinRT = aggregateReport, WinRTIndividual = individualReports, QueryMilliseconds = sampleWatch.Elapsed.TotalMilliseconds };
    var json = JsonSerializer.Serialize(row, new JsonSerializerOptions { IncludeFields = true });
    log.WriteLine(json);
    var availability = $"{ac}/{reading.PowerSource}/{reading.LeftSource}/{reading.FullSource}/{systemWatts.HasValue}/{aggregateReport.Watts.HasValue}";
    if (transition || firstValid.Count > 0 || recoveredAfterUnknown.Count > 0 || availability != previousAvailability || now - lastPrint >= 10)
    { Console.WriteLine(json); lastPrint = now; }
    previousAvailability = availability; previousAc = ac;
    try { await Task.Delay(1000, cancellation.Token); }
    catch (OperationCanceledException) { break; }
}
Console.WriteLine("진단 종료. 핸들을 해제합니다.");

static double? ValidWatts(int? rate, bool charging, bool discharging, byte? ac)
{
    if (rate is null or 0 or int.MinValue || Math.Abs((double)rate.Value) > 999900) return null;
    if (ac == 0 && rate > 0) return null;
    if ((charging && !discharging && rate > 0) || (discharging && !charging && rate < 0))
        return Math.Abs((double)rate.Value) / 1000;
    return null;
}

static Report ReadReport(Battery? battery, byte? ac, string? initializationError)
{
    if (battery is null) return new(false, null, null, null, null, null, initializationError ?? "Battery object unavailable");
    try
    {
        var report = battery.GetReport();
        return new(true, report.Status.ToString(), report.ChargeRateInMilliwatts,
            report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours,
            ValidWatts(report.ChargeRateInMilliwatts, report.Status == BatteryStatus.Charging,
                report.Status == BatteryStatus.Discharging, ac), null);
    }
    catch (Exception exception) { return new(false, null, null, null, null, null, $"0x{exception.HResult:X8}: {exception.Message}"); }
}

static object?[] ReadCachedTags(SystemBatterySource source)
{
    var devices = (System.Collections.IEnumerable)typeof(SystemBatterySource).GetField("_devices", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(source)!;
    return devices.Cast<object>().Select(cached =>
    {
        var device = cached.GetType().GetProperty("Device")!.GetValue(cached)!;
        return device.GetType().GetField("_tag", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(device);
    }).ToArray();
}

internal sealed record Report(bool QuerySucceeded, string? Status, int? ChargeRateInMilliwatts,
    int? RemainingCapacityInMilliwattHours, int? FullChargeCapacityInMilliwattHours, double? Watts, string? Error);

[StructLayout(LayoutKind.Sequential)]
internal struct SystemState
{
    public byte AcOnLine, BatteryPresent, Charging, Discharging, Spare1, Spare2, Spare3, Tag;
    public uint MaxCapacity, RemainingCapacity, Rate, EstimatedTime, DefaultAlert1, DefaultAlert2;
}
internal static class NativePower
{
    // SystemBatteryState=5, 입력 NULL. 정책 변경 API/쓰기 모드는 호출하지 않는다.
    [DllImport("powrprof.dll")]
    internal static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, out SystemState output, uint outputLength);
}
