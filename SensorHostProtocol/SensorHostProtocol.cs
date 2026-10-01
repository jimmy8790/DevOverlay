using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevOverlay.SensorHostProtocol;

public enum SensorHostOperation { Hello, GetCpuPackageSnapshot, Shutdown, GetStatus }
public enum SensorHostMessageKind { Hello, CpuPackageSnapshot, Error, Goodbye, Status }
public enum SensorHostState { NotRunning, Starting, ElevationCancelled, Connecting, Connected, Ready, PawnIoUnavailable, PawnIoAccessDenied, CpuHardwareUnavailable, CpuSensorsUnavailable, Disconnected, Failed, ProtocolMismatch }

public sealed record SensorHostRequest(int Version, SensorHostOperation Operation);
public sealed record SensorHostResponse(
    int Version,
    SensorHostMessageKind Kind,
    SensorHostState State,
    double? TemperatureCelsius = null,
    double? PackagePowerWatts = null,
    DateTimeOffset? TimestampUtc = null,
    string? Detail = null,
    bool TemperatureSensorFound = false,
    bool PowerSensorFound = false);

/// <summary>Only fixed CPU package operations cross the elevated boundary.</summary>
public static class SensorHostWire
{
    public const int Version = 1;
    public const int MaxMessageBytes = 4096;
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static Task WriteRequestAsync(Stream stream, SensorHostRequest request, CancellationToken token) => WriteAsync(stream, request, token);
    public static Task WriteResponseAsync(Stream stream, SensorHostResponse response, CancellationToken token) => WriteAsync(stream, response, token);
    public static Task<SensorHostRequest> ReadRequestAsync(Stream stream, CancellationToken token) => ReadAsync<SensorHostRequest>(stream, token);
    public static Task<SensorHostResponse> ReadResponseAsync(Stream stream, CancellationToken token) => ReadAsync<SensorHostResponse>(stream, token);

    private static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (payload.Length > MaxMessageBytes) throw new InvalidDataException("SensorHost message is too large.");
        byte[] length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] length = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length, token);
        int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > MaxMessageBytes) throw new InvalidDataException("Invalid SensorHost message length.");
        byte[] payload = new byte[count];
        await stream.ReadExactlyAsync(payload, token);
        try { return JsonSerializer.Deserialize<T>(payload, Options) ?? throw new InvalidDataException("Empty SensorHost message."); }
        catch (JsonException exception) { throw new InvalidDataException("Malformed SensorHost message.", exception); }
    }
}
