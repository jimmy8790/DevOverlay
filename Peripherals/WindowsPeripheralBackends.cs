using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DevOverlay.Peripherals;

internal static class PeripheralWindowsMetadata
{
    internal const string HidSelector = "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\"";
    internal static readonly string[] Properties = ["System.Devices.ContainerId", "System.Devices.Aep.ContainerId",
        "System.Devices.BatteryLife", "System.Devices.Aep.IsPresent", "System.Devices.Aep.IsConnected",
        "System.Devices.Connected", "System.Devices.CategoryIds", "System.Devices.LocalMachine", "System.DeviceInterface.Hid.UsagePage",
        "System.DeviceInterface.Hid.UsageId", "System.Devices.Aep.Bluetooth.Le.Appearance"];
    internal static object? Get(DeviceInformation device, string property) => device.Properties.GetValueOrDefault(property);
    internal static double? Numeric(object? value) => value switch
    {
        byte v => v, ushort v => v, short v => v, uint v => v, int v => v, long v => v,
        float v => v, double v => v, _ => null
    };
    internal static string? Container(DeviceInformation device) =>
        (Get(device, "System.Devices.Aep.ContainerId") ?? Get(device, "System.Devices.ContainerId"))?.ToString();
    internal static bool Connected(DeviceInformation device) =>
        (Get(device, "System.Devices.Aep.IsConnected") ?? Get(device, "System.Devices.Connected") ??
            Get(device, "System.Devices.Aep.IsPresent")) is true;
    internal static PeripheralType Classify(DeviceInformation device)
    {
        var page = Numeric(Get(device, "System.DeviceInterface.Hid.UsagePage"));
        var usage = Numeric(Get(device, "System.DeviceInterface.Hid.UsageId"));
        if (page == 1) return usage switch
        { 2 => PeripheralType.Mouse, 6 => PeripheralType.Keyboard, 4 or 5 => PeripheralType.Controller, _ => PeripheralType.Other };
        // Bluetooth SIG HID appearance: category 15, subcategory keyboard/mouse/joystick/gamepad.
        var appearance = Numeric(Get(device, "System.Devices.Aep.Bluetooth.Le.Appearance"));
        if (appearance.HasValue && ((int)appearance.Value >> 6) == 15)
            return ((int)appearance.Value & 63) switch
            { 1 => PeripheralType.Keyboard, 2 => PeripheralType.Mouse, 3 or 4 => PeripheralType.Controller, _ => PeripheralType.Other };
        var categories = Get(device, "System.Devices.CategoryIds") switch
        { string[] values => values, string value => [value], _ => Array.Empty<string>() };
        var mouse = categories.Any(value => value.Equals("Input.Mouse", StringComparison.OrdinalIgnoreCase));
        var keyboard = categories.Any(value => value.Equals("Input.Keyboard", StringComparison.OrdinalIgnoreCase));
        if (mouse && !keyboard) return PeripheralType.Mouse;
        if (keyboard && !mouse) return PeripheralType.Keyboard;
        if (categories.Any(value => value.Equals("Input.Gaming", StringComparison.OrdinalIgnoreCase))) return PeripheralType.Controller;
        if (categories.Any(value => value.Equals("Audio.Headphone", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Audio.Headset", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("Communication.Headset", StringComparison.OrdinalIgnoreCase))) return PeripheralType.Headset;
        // 명확한 제품 유형 이름만 보조 판단한다. 알 수 없는 오디오 장치를 헤드셋으로 단정하지 않는다.
        var name = device.Name;
        if (name.Contains("earbud", StringComparison.OrdinalIgnoreCase)) return PeripheralType.Earbuds;
        if (name.Contains("headset", StringComparison.OrdinalIgnoreCase) || name.Contains("헤드셋")) return PeripheralType.Headset;
        if (name.Contains("keyboard", StringComparison.OrdinalIgnoreCase) || name.Contains("키보드")) return PeripheralType.Keyboard;
        if (name.Contains("mouse", StringComparison.OrdinalIgnoreCase) || name.Contains("마우스")) return PeripheralType.Mouse;
        return PeripheralType.Other;
    }
    internal static PeripheralObservation Observation(DeviceInformation device, PeripheralBatterySource source,
        double? percentage, bool connected, string status, string? container = null) =>
        new(device.Id, container ?? Container(device), device.Name, Classify(device), connected,
            PeripheralBatteryMerge.IsPercentage(percentage) ? percentage : null, null, source, DateTimeOffset.UtcNow, status);
}

internal sealed class WindowsBatteryPropertyBackend : IPeripheralBatteryBackend, IDisposable
{
    private readonly List<DeviceWatcher> _watchers = [];
    internal IReadOnlyList<DeviceInformation> HidDevices { get; private set; } = [];
    internal event Action? DevicesChanged;
    public string Name => "Windows numeric battery property";
    internal void StartWatching()
    {
        AddWatcher(PeripheralWindowsMetadata.HidSelector, DeviceInformationKind.DeviceInterface);
        AddWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), DeviceInformationKind.AssociationEndpoint);
        AddWatcher("", DeviceInformationKind.DeviceContainer);
    }
    private void AddWatcher(string selector, DeviceInformationKind kind)
    {
        try
        {
            var watcher = DeviceInformation.CreateWatcher(selector, PeripheralWindowsMetadata.Properties, kind);
            watcher.Added += (_, _) => DevicesChanged?.Invoke();
            watcher.Removed += (_, _) => DevicesChanged?.Invoke();
            watcher.Updated += (_, _) => DevicesChanged?.Invoke();
            _watchers.Add(watcher); watcher.Start();
        }
        catch (Exception exception) { DevOverlay.Platform.Windows.RuntimeDiagnostics.Write($"[Peripheral] Watcher={kind} Failure={exception.GetType().Name}"); }
    }
    public async Task<IReadOnlyList<PeripheralObservation>> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new List<PeripheralObservation>();
        var containers = await DeviceInformation.FindAllAsync("", PeripheralWindowsMetadata.Properties,
            DeviceInformationKind.DeviceContainer).AsTask(cancellationToken).ConfigureAwait(false);
        var localContainers = containers.Where(device => PeripheralWindowsMetadata.Get(device, "System.Devices.LocalMachine") is true ||
            (PeripheralWindowsMetadata.Get(device, "System.Devices.CategoryIds") as string[] ?? []).Any(category =>
                category.StartsWith("Communication.Phone", StringComparison.OrdinalIgnoreCase)))
            .Select(device => Guid.TryParse(device.Id, out var id) ? id.ToString("D") : device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containerTypes = containers.ToDictionary(device => Guid.TryParse(device.Id, out var id) ? id.ToString("D") : device.Id,
            PeripheralWindowsMetadata.Classify, StringComparer.OrdinalIgnoreCase);
        foreach (var device in containers)
        {
            if (localContainers.Contains(Guid.TryParse(device.Id, out var id) ? id.ToString("D") : device.Id)) continue;
            var battery = PeripheralWindowsMetadata.Numeric(PeripheralWindowsMetadata.Get(device, "System.Devices.BatteryLife"));
            var categories = PeripheralWindowsMetadata.Get(device, "System.Devices.CategoryIds") as string[] ?? [];
            if (!battery.HasValue && PeripheralWindowsMetadata.Classify(device) == PeripheralType.Other &&
                !categories.Any(value => value.StartsWith("Input", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("Audio", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("Communication.Headset", StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(PeripheralWindowsMetadata.Observation(device, PeripheralBatterySource.WindowsProperty,
                battery, PeripheralWindowsMetadata.Connected(device), battery is >= 0 and <= 100 ? "Ready" : "No numeric battery property", device.Id));
        }
        var hid = await DeviceInformation.FindAllAsync(PeripheralWindowsMetadata.HidSelector,
            PeripheralWindowsMetadata.Properties).AsTask(cancellationToken).ConfigureAwait(false);
        HidDevices = hid.Where(device => !localContainers.Contains(PeripheralWindowsMetadata.Container(device)?.Trim('{', '}') ?? "")).ToArray();
        foreach (var device in HidDevices)
        {
            var type = PeripheralWindowsMetadata.Classify(device);
            var containerType = containerTypes.GetValueOrDefault(PeripheralWindowsMetadata.Container(device)?.Trim('{', '}') ?? "", PeripheralType.Other);
            if (containerTypes.ContainsKey(PeripheralWindowsMetadata.Container(device)?.Trim('{', '}') ?? "")) type = containerType;
            var battery = PeripheralWindowsMetadata.Numeric(PeripheralWindowsMetadata.Get(device, "System.Devices.BatteryLife"));
            if (type == PeripheralType.Other && !battery.HasValue) continue;
            result.Add(PeripheralWindowsMetadata.Observation(device, PeripheralBatterySource.WindowsProperty,
                battery, device.IsEnabled, battery is >= 0 and <= 100 ? "Ready" : "No numeric battery property") with { Type = type });
        }
        return result;
    }
    public void Dispose()
    {
        foreach (var watcher in _watchers)
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        _watchers.Clear();
    }
}

internal sealed class BluetoothGattBatteryBackend : IPeripheralBatteryBackend
{
    public string Name => "Bluetooth standard Battery Service";
    public async Task<IReadOnlyList<PeripheralObservation>> CollectAsync(CancellationToken cancellationToken)
    {
        var devices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            PeripheralWindowsMetadata.Properties, DeviceInformationKind.AssociationEndpoint)
            .AsTask(cancellationToken).ConfigureAwait(false);
        var results = new List<PeripheralObservation>();
        foreach (var info in devices)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var connected = PeripheralWindowsMetadata.Connected(info);
            var windowsPercentage = PeripheralWindowsMetadata.Numeric(PeripheralWindowsMetadata.Get(info, "System.Devices.BatteryLife"));
            results.Add(PeripheralWindowsMetadata.Observation(info, PeripheralBatterySource.WindowsProperty,
                windowsPercentage, connected, PeripheralBatteryMerge.IsPercentage(windowsPercentage) ? "Ready" : "No numeric battery property"));
            if (!connected || PeripheralBatteryMerge.IsPercentage(windowsPercentage)) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                results.Add(await ReadAsync(info, timeout.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { results.Add(PeripheralWindowsMetadata.Observation(info, PeripheralBatterySource.BluetoothGatt, null, connected, "Bluetooth timeout")); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            { results.Add(PeripheralWindowsMetadata.Observation(info, PeripheralBatterySource.BluetoothGatt, null, connected, exception.GetType().Name)); }
            // 전체 backend 시간 예산이 끝나도 이미 읽힌 다른 기기의 결과는 유지한다.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
        return results;
    }
    private static async Task<PeripheralObservation> ReadAsync(DeviceInformation info, CancellationToken token)
    {
        // 연결 중인 기존 paired 장치만 읽는다. pairing/연결 유지 요청/쓰기 API는 호출하지 않는다.
        using var device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(token).ConfigureAwait(false);
        if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            return PeripheralWindowsMetadata.Observation(info, PeripheralBatterySource.BluetoothGatt, null, false, "Disconnected or access denied");
        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached)
            .AsTask(token).ConfigureAwait(false);
        var values = new List<byte>();
        try
        {
            if (services.Status != GattCommunicationStatus.Success)
                return PeripheralWindowsMetadata.Observation(info, PeripheralBatterySource.BluetoothGatt, null, true, services.Status.ToString());
            foreach (var service in services.Services)
            {
                var characteristics = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel,
                    BluetoothCacheMode.Cached).AsTask(token).ConfigureAwait(false);
                if (characteristics.Status != GattCommunicationStatus.Success) continue;
                foreach (var characteristic in characteristics.Characteristics)
                {
                    var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
                    if (result.Status == GattCommunicationStatus.Success && result.Value.Length == 1)
                    {
                        using var reader = DataReader.FromBuffer(result.Value);
                        var value = reader.ReadByte(); if (value <= 100) values.Add(value);
                    }
                }
            }
        }
        finally { foreach (var service in services.Services) service.Dispose(); }
        // 복수 배터리는 좌/우/케이스 의미가 다를 수 있으므로 임의 평균/최솟값을 만들지 않는다.
        return PeripheralWindowsMetadata.Observation(info, PeripheralBatterySource.BluetoothGatt,
            values.Count == 1 ? values[0] : null, true,
            values.Count == 1 ? "Ready" : values.Count > 1 ? "Multiple batteries; no single percentage" : "Battery Service unavailable");
    }
}

internal sealed class StandardHidBatteryBackend(WindowsBatteryPropertyBackend inventory) : IPeripheralBatteryBackend
{
    public string Name => "Standard HID Battery Strength";
    public async Task<IReadOnlyList<PeripheralObservation>> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new List<PeripheralObservation>();
        foreach (var info in inventory.HidDevices)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!info.IsEnabled) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                using var device = await HidDevice.FromIdAsync(info.Id, FileAccessMode.Read).AsTask(timeout.Token).ConfigureAwait(false);
                if (device is null)
                {
                    result.Add(PeripheralWindowsMetadata.Observation(info, PeripheralBatterySource.StandardHid,
                        null, true, "Standard HID access unavailable"));
                    continue;
                }
                // Generic Device Controls(0x06), Battery Strength(0x20) descriptor만 읽는다.
                var descriptions = device.GetNumericControlDescriptions(HidReportType.Feature, 0x06, 0x20);
                var descriptor = descriptions.FirstOrDefault(item => item.LogicalMinimum == 0 && item.LogicalMaximum == 100 &&
                    item.IsAbsolute && item.Unit == 0 && item.UnitExponent == 0);
                if (descriptor is null) continue;
                var report = await device.GetFeatureReportAsync(descriptor.ReportId).AsTask(timeout.Token).ConfigureAwait(false);
                var value = report.GetNumericControlByDescription(descriptor).Value;
                result.Add(PeripheralWindowsMetadata.Observation(info, PeripheralBatterySource.StandardHid,
                    value, true, PeripheralBatteryMerge.IsPercentage(value) ? "Ready" : "Invalid Battery Strength"));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            { result.Add(PeripheralWindowsMetadata.Observation(info, PeripheralBatterySource.StandardHid, null, true, exception.GetType().Name)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
        return result;
    }
}
