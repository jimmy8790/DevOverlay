using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration;

namespace DevOverlay.Peripherals.VendorBackends;

internal sealed record HidInterfaceMetadata(ushort VendorId, ushort ProductId, ushort UsagePage, ushort UsageId,
    ushort InputLength, ushort OutputLength, ushort FeatureLength, ushort InputValueCaps, ushort InputButtonCaps,
    bool Accessible, int Win32Error, byte? InputReportId = null, byte? OutputReportId = null);

internal sealed record PeripheralHidInterface(string Path, string? ContainerId, string FriendlyName, HidInterfaceMetadata Metadata,
    double? WindowsBatteryProperty = null)
{
    internal string Identity => PeripheralObservation.StableIdentity(ContainerId, Path);
}

internal interface IHidBatteryTransport
{
    Task<byte[]> QueryAsync(PeripheralHidInterface device, PulsarReadCommand command, CancellationToken token);
}
internal interface IRazerBatteryTransport
{
    Task<byte[]> QueryAsync(PeripheralHidInterface device, RazerPaReadCommand command, CancellationToken token);
}

// Native HID 접근은 desktop WinRT capability 제한과 분리된다. 키보드/마우스 입력 collection을 점유하지 않는다.
internal sealed class WindowsHidTransport : IHidBatteryTransport, IRazerBatteryTransport
{
    internal static HidInterfaceMetadata Inspect(DeviceInformation info)
    {
        using var handle = CreateFile(info.Id, 0, 3, 0, 3, 0, 0);
        if (handle.IsInvalid) return new(0, 0, 0, 0, 0, 0, 0, 0, 0, false, Marshal.GetLastWin32Error());
        var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes) || !HidD_GetPreparsedData(handle, out var preparsed))
            return new(0, 0, 0, 0, 0, 0, 0, 0, 0, false, Marshal.GetLastWin32Error());
        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != 0x00110000)
                return new(attributes.VendorId, attributes.ProductId, 0, 0, 0, 0, 0, 0, 0, false, 13);
            return new(attributes.VendorId, attributes.ProductId, caps.UsagePage, caps.Usage,
                caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength,
                caps.NumberInputValueCaps, caps.NumberInputButtonCaps, true, 0,
                SingleReportId(preparsed, 0, caps.NumberInputButtonCaps, caps.NumberInputValueCaps),
                SingleReportId(preparsed, 1, caps.NumberOutputButtonCaps, caps.NumberOutputValueCaps));
        }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    internal static IReadOnlyList<PeripheralHidInterface> Inventory(IEnumerable<DeviceInformation> devices) => devices
        .Select(info => new PeripheralHidInterface(info.Id, PeripheralWindowsMetadata.Container(info), info.Name, Inspect(info),
            PeripheralWindowsMetadata.Numeric(PeripheralWindowsMetadata.Get(info, "System.Devices.BatteryLife")))).ToArray();

    private static byte? SingleReportId(nint preparsed, int reportType, ushort buttons, ushort values)
    {
        var ids = new HashSet<byte>();
        // HIDP_BUTTON_CAPS/HIDP_VALUE_CAPS는 모두 72 bytes, ReportID는 offset 2다.
        foreach (var isButton in new[] { true, false })
        {
            var count = isButton ? buttons : values;
            if (count == 0) continue;
            var buffer = Marshal.AllocHGlobal(checked(72 * count));
            try
            {
                var status = isButton ? HidP_GetButtonCaps(reportType, buffer, ref count, preparsed) :
                    HidP_GetValueCaps(reportType, buffer, ref count, preparsed);
                if (status != 0x00110000) return null;
                for (var index = 0; index < count; index++) ids.Add(Marshal.ReadByte(buffer, index * 72 + 2));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return ids.Count == 1 ? ids.Single() : null;
    }

    public async Task<byte[]> QueryAsync(PeripheralHidInterface device, PulsarReadCommand command, CancellationToken token)
    {
        // 미검증 PID/collection에는 보내지 않는다. 임의 byte[] 대신 읽기 명령 whitelist만 허용한다.
        if (!PulsarNordicProtocol.Matches(device.Metadata)) throw new NotSupportedException("Unverified HID interface");
        var request = PulsarNordicProtocol.Request(command);
        return await ExchangeAsync(device.Path, request, buffer => PulsarNordicProtocol.IsReply(buffer, command), token).ConfigureAwait(false);
    }

    public Task<byte[]> QueryAsync(PeripheralHidInterface device, RazerPaReadCommand command, CancellationToken token)
    {
        if (!RazerBarracudaPaProtocol.Matches(device.Metadata)) throw new NotSupportedException("Unverified HID interface");
        return ExchangeAsync(device.Path, RazerBarracudaPaProtocol.Request(command),
            buffer => RazerBarracudaPaProtocol.Value(buffer, command).HasValue, token);
    }

    private static async Task<byte[]> ExchangeAsync(string path, byte[] request, Func<byte[], bool> isReply, CancellationToken token)
    {
        using var handle = CreateFile(path, 0xC0000000, 3, 0, 3, 0x40000000, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        await using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
        await stream.WriteAsync(request, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        // 같은 collection의 DPI/profile 이벤트는 배터리 응답이 아니다. 유한 개수와 호출자의 timeout으로 제한한다.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = new byte[request.Length];
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (isReply(buffer[..count])) return buffer[..count];
        }
        return [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes { internal int Size; internal ushort VendorId, ProductId, VersionNumber; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        internal ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
        internal ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        internal ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        internal ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint data, out HidCaps caps);
    [DllImport("hid.dll")]
    private static extern int HidP_GetButtonCaps(int reportType, nint caps, ref ushort count, nint data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, nint caps, ref ushort count, nint data);
}
