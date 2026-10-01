using System.Text.Json;
using System.IO;

namespace DevOverlay.Metrics.Windows;

/// <summary>Receives only Claude Code's documented statusLine JSON and stores a small per-user telemetry cache.</summary>
internal static class ClaudeStatusLineBridge
{
    internal const string Argument = "--claude-status-line";

    public static bool IsInvocation(IEnumerable<string> arguments) => arguments.Any(argument =>
        string.Equals(argument, Argument, StringComparison.OrdinalIgnoreCase));

    public static void Run(TextReader input, ClaudeStatusLineStateStore? store = null)
    {
        try
        {
            using var document = JsonDocument.Parse(input.ReadToEnd());
            if (TryCreateState(document.RootElement, out var state)) (store ?? new ClaudeStatusLineStateStore()).Write(state);
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool TryCreateState(JsonElement root, out ClaudeStatusLineState state)
    {
        state = default!;
        if (root.ValueKind != JsonValueKind.Object) return false;
        int? context = ReadPercent(root, "context_window", "remaining_percentage");
        int? fiveHour = ReadPercent(root, "rate_limits", "five_hour", "used_percentage");
        int? sevenDay = ReadPercent(root, "rate_limits", "seven_day", "used_percentage");
        if (context is null && fiveHour is null && sevenDay is null) return false;
        state = new ClaudeStatusLineState(1, DateTimeOffset.UtcNow, context, fiveHour, sevenDay);
        return true;
    }

    private static int? ReadPercent(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        if (current.ValueKind != JsonValueKind.Number || !current.TryGetDouble(out var value) ||
            double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 100) return null;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}

internal sealed record ClaudeStatusLineState(int SchemaVersion, DateTimeOffset UpdatedAtUtc, int? ContextRemainingPercent,
    int? FiveHourUsedPercent, int? SevenDayUsedPercent);

internal sealed class ClaudeStatusLineStateStore(string? filePath = null)
{
    public string FilePath { get; } = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevOverlay", "Claude", "statusline-state.json");

    public void Write(ClaudeStatusLineState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public ClaudeStatusLineState? Read()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var state = JsonSerializer.Deserialize<ClaudeStatusLineState>(stream);
            return state is { SchemaVersion: 1 } ? state : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }
}
