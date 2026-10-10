using System.Text.Json;
using DevOverlay.Metrics.Windows;

// 기존 실측 데이터 재생이다. 새 물리 전환 실측으로 해석하면 안 된다.
internal static class BatteryTraceReplay
{
    internal static void Run(string path)
    {
        var estimator = new BatteryPowerEstimator();
        foreach (var line in File.ReadLines(path))
        {
            using var json = JsonDocument.Parse(line);
            var row = json.RootElement;
            if (row.GetProperty("Kind").GetString() != "Sample") continue;
            var ioctl = row.GetProperty("IOCTL");
            var devices = ioctl.GetProperty("Devices");
            var flow = Enum.Parse<BatteryFlow>(row.GetProperty("Flow").GetString()!);
            var now = row.GetProperty("Time").GetDateTimeOffset();
            BatteryPowerEstimate estimate;
            if (devices.GetArrayLength() != 1) estimate = estimator.Reset("UnsupportedBatterySet");
            else
            {
                var device = devices[0];
                var caps = device.GetProperty("Capabilities").GetUInt32();
                var status = device.GetProperty("Status");
                if (status.ValueKind != JsonValueKind.Object || (caps & 0x40000000) != 0)
                    estimate = estimator.Reset("InvalidCapacityOrUnits");
                else estimate = estimator.Observe(now, $"trace/{ioctl.GetProperty("Tags")[0]}/{device.GetProperty("FullCapacity")}",
                    flow, status.GetProperty("Capacity").GetUInt32());
            }
            var nativeValue = ioctl.TryGetProperty("NativeWatts", out var native) ? native : ioctl.GetProperty("Watts");
            double? nativeWatts = nativeValue.ValueKind == JsonValueKind.Number ? nativeValue.GetDouble() : null;
            Console.WriteLine(JsonSerializer.Serialize(new { Replay = true, Time = now,
                SinceTransitionSeconds = row.GetProperty("SinceTransitionSeconds"), Flow = flow.ToString(),
                PowerSource = nativeWatts.HasValue ? "Native" : estimate.Watts.HasValue ? "Estimated" : "Unavailable",
                Watts = nativeWatts ?? estimate.Watts, NativeWatts = nativeWatts, Estimate = estimate,
                AbsoluteError = nativeWatts.HasValue && estimate.Watts.HasValue ? Math.Abs(Math.Abs(nativeWatts.Value) - Math.Abs(estimate.Watts.Value)) : (double?)null, // 과거 로그의 NativeWatts는 크기값이라 크기로 비교한다.
                PercentageError = nativeWatts is not null and not 0 && estimate.Watts.HasValue ? Math.Abs(Math.Abs(nativeWatts.Value) - Math.Abs(estimate.Watts.Value)) / Math.Abs(nativeWatts.Value) * 100 : (double?)null }));
        }
    }
}
