using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevOverlay.Platform.Windows;

namespace DevOverlay.Metrics.Windows;

internal sealed record ClaudeQuotaWindow(int UsedPercent, DateTimeOffset? ResetAtUtc);

internal sealed record ClaudeUsageCommandResult(ClaudeQuotaWindow? FiveHour, ClaudeQuotaWindow? SevenDay);

internal enum ClaudeUsageQueryFailure { None, NotFound, Process, Parse }

internal interface IClaudeUsageCommandClient
{
    ClaudeUsageQueryFailure LastFailure { get; }
    Task<ClaudeUsageCommandResult?> QueryAsync(CancellationToken cancellationToken);
}

/// <summary>Runs the documented local Claude Code usage command; it never reads credentials, chat history, or Desktop state.</summary>
internal sealed class ClaudeUsageCommandClient(CodexExecutableResolver resolver) : IClaudeUsageCommandClient
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
    private readonly CodexExecutableResolver _resolver = resolver;
    public ClaudeUsageQueryFailure LastFailure { get; private set; }

    public async Task<ClaudeUsageCommandResult?> QueryAsync(CancellationToken cancellationToken)
    {
        LastFailure = ClaudeUsageQueryFailure.None;
        var command = _resolver.Resolve();
        if (command is null)
        {
            LastFailure = ClaudeUsageQueryFailure.NotFound;
            RuntimeDiagnostics.Write("[Claude] Stage=Resolve Result=NotFound");
            return null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        Process? process = null;
        try
        {
            RuntimeDiagnostics.Write($"[Claude] Stage=Launch Kind={command.Kind} Source={command.Source} MergedChildPath={!string.IsNullOrWhiteSpace(command.ChildPath)}");
            process = new Process { StartInfo = command.CreateUsageStartInfo() };
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            _ = await errorTask;
            var parsed = ClaudeUsageCommandParser.TryParse(output, DateTimeOffset.UtcNow, out var result);
            RuntimeDiagnostics.Write($"[Claude] Stage=Result ExitCode={process.ExitCode} StdoutCharacters={output.Length} Parsed={parsed}");
            if (process.ExitCode != 0 || !parsed)
            {
                LastFailure = process.ExitCode == 0 ? ClaudeUsageQueryFailure.Parse : ClaudeUsageQueryFailure.Process;
                return null;
            }
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LastFailure = ClaudeUsageQueryFailure.Process;
            RuntimeDiagnostics.Write("[Claude] The local /usage command timed out.");
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            LastFailure = ClaudeUsageQueryFailure.Process;
            RuntimeDiagnostics.Write($"[Claude] The local /usage command failed: {exception.GetType().Name}");
            return null;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            process?.Dispose();
        }
    }
}

internal static partial class ClaudeUsageCommandParser
{
    private static readonly string[] ResetDateFormats =
    [
        "MMM d, htt", "MMM d htt",
        "MMM d, h:mmtt", "MMM d h:mmtt",
        "MMM d, h:mm tt", "MMM d h:mm tt"
    ];
    [GeneratedRegex(@"(?im)^\s*Current\s+session\s*:\s*(?<used>\d{1,3})\s*%\s*used\b(?<tail>.*)$")]
    private static partial Regex SessionLine();
    [GeneratedRegex(@"(?im)^\s*Current\s+week(?:\s*\([^)]*\))?\s*:\s*(?<used>\d{1,3})\s*%\s*used\b(?<tail>.*)$")]
    private static partial Regex WeekLine();
    [GeneratedRegex(@"resets\s+(?<date>.+?)(?:\s*\((?<zone>[^)]+)\))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResetTail();

    public static bool TryParse(string json, DateTimeOffset now, out ClaudeUsageCommandResult result)
    {
        result = default!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("is_error", out var error) && error.ValueKind != JsonValueKind.False ||
                root.TryGetProperty("num_turns", out var turns) && (!turns.TryGetInt32(out var turnCount) || turnCount != 0) ||
                !root.TryGetProperty("local_command", out var command) || command.GetString() != "usage" ||
                !root.TryGetProperty("result", out var text) || text.ValueKind != JsonValueKind.String) return false;
            var value = text.GetString();
            if (string.IsNullOrWhiteSpace(value)) return false;
            var fiveHour = ParseLine(SessionLine().Match(value), now);
            var sevenDay = ParseLine(WeekLine().Match(value), now);
            if (fiveHour is null && sevenDay is null) return false;
            result = new ClaudeUsageCommandResult(fiveHour, sevenDay);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static ClaudeQuotaWindow? ParseLine(Match match, DateTimeOffset now)
    {
        if (!match.Success || !int.TryParse(match.Groups["used"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var used) || used is < 0 or > 100) return null;
        return new ClaudeQuotaWindow(used, TryParseReset(match.Groups["tail"].Value, now));
    }

    private static DateTimeOffset? TryParseReset(string tail, DateTimeOffset now)
    {
        var match = ResetTail().Match(tail);
        if (!match.Success || !DateTime.TryParseExact(match.Groups["date"].Value.Trim(), ResetDateFormats,
                CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var local)) return null;
        try
        {
            var zone = match.Groups["zone"].Value.Trim();
            var timeZone = zone.Equals("Asia/Seoul", StringComparison.OrdinalIgnoreCase) ? TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time") : TimeZoneInfo.Local;
            var candidate = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeZone.GetUtcOffset(local));
            return candidate < now.AddDays(-1) ? candidate.AddYears(1) : candidate;
        }
        catch (TimeZoneNotFoundException) { return null; }
    }
}

internal sealed record ClaudeAccountQuotaState(int SchemaVersion, DateTimeOffset LastSuccessfulQueryUtc, ClaudeQuotaWindow? FiveHour, ClaudeQuotaWindow? SevenDay);

internal sealed class ClaudeAccountQuotaStateStore(string? filePath = null)
{
    private readonly string _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevOverlay", "Claude", "account-quota-state.json");
    public void Write(ClaudeAccountQuotaState state)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporary = _filePath + ".tmp";
        using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, state);
        File.Move(temporary, _filePath, true);
    }
    public ClaudeAccountQuotaState? Read()
    {
        try
        {
            using var stream = File.OpenRead(_filePath);
            var state = JsonSerializer.Deserialize<ClaudeAccountQuotaState>(stream);
            return state?.SchemaVersion == 1 ? state : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
