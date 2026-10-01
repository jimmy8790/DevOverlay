using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DevOverlay.Platform.Windows;

namespace DevOverlay.Metrics.Windows;

public enum CodexAppServerFailure
{
    CliNotFound,
    ProtocolUnsupported,
    Connection,
    Timeout,
    Error
}

public enum CodexRefreshReason { PollInterval, Notification, ProcessExited }

public sealed class CodexAppServerException(CodexAppServerFailure failure, string message) : Exception(message)
{
    public CodexAppServerFailure Failure { get; } = failure;
}

public interface ICodexAppServerClient : IAsyncDisposable
{
    event Action? RateLimitsUpdated;
    event Action? Exited;
    Task<CodexRateLimitSnapshot> StartAndReadAsync(CancellationToken cancellationToken);
    Task<CodexRateLimitSnapshot> ReadRateLimitsAsync(CancellationToken cancellationToken);
    Task<CodexRefreshReason> WaitForRefreshAsync(TimeSpan pollInterval, CancellationToken cancellationToken);
}

/// <summary>One owned local `codex app-server --stdio` process. Stdout is JSON-RPC only and stderr is drained, never persisted.</summary>
public sealed class CodexAppServerClient : ICodexAppServerClient
{
    private const int RequestTimeoutSeconds = 10;
    private readonly CodexExecutableResolver _resolver;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource<CodexRefreshReason> _notificationSignal = NewRefreshSignal();
    private readonly TaskCompletionSource<CodexRefreshReason> _exitSignal = NewRefreshSignal();
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private long _nextRequestId;
    private bool _disposed;

    public CodexAppServerClient() : this(new CodexExecutableResolver()) { }

    internal CodexAppServerClient(CodexExecutableResolver resolver) => _resolver = resolver;

    public event Action? RateLimitsUpdated;
    public event Action? Exited;
    /// <summary>Diagnostics only; identifies the exact child this instance owns, never arbitrary Codex processes.</summary>
    public int? OwnedProcessId => _process is { HasExited: false } process ? process.Id : null;
    internal CodexLaunchCommand? LaunchCommand { get; private set; }

    public async Task<CodexRateLimitSnapshot> StartAndReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_process is not null) return await ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = _resolver.Resolve() ?? throw new CodexAppServerException(
                CodexAppServerFailure.CliNotFound, "Codex CLI was not found in the process, user, or machine PATH.");
            LaunchCommand = command;
            RuntimeDiagnostics.Write($"[Codex] Resolved Type={command.Kind} Extension={command.Extension} PathSource={command.Source}");
            var process = new Process
            {
                StartInfo = command.CreateAppServerStartInfo(),
                EnableRaisingEvents = true
            };
            process.Exited += OnProcessExited;
            if (!process.Start()) throw new CodexAppServerException(CodexAppServerFailure.Connection, "Codex App Server could not start.");
            _process = process;
            _stdoutTask = ReadStdoutAsync(process, _shutdown.Token);
            _stderrTask = DrainStderrAsync(process, _shutdown.Token);

            await RequestAsync("initialize", new
            {
                clientInfo = new { name = "dev-overlay", version = typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString() ?? "unknown" },
                capabilities = new { optOutNotificationMethods = Array.Empty<string>() }
            }, cancellationToken).ConfigureAwait(false);
            await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            return await ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new CodexAppServerException(CodexAppServerFailure.Connection, "Codex CLI was found but could not start.");
        }
    }

    public async Task<CodexRateLimitSnapshot> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("account/rateLimits/read", new { excludeResetCreditDetails = true }, cancellationToken).ConfigureAwait(false);
        return CodexRateLimitResponseParser.Parse(result);
    }

    public async Task<CodexRefreshReason> WaitForRefreshAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var delay = Task.Delay(pollInterval, cancellationToken);
        var notification = Volatile.Read(ref _notificationSignal);
        var completed = await Task.WhenAny(delay, notification.Task, _exitSignal.Task).ConfigureAwait(false);
        if (completed == delay)
        {
            await delay.ConfigureAwait(false);
            return CodexRefreshReason.PollInterval;
        }
        var reason = await ((Task<CodexRefreshReason>)completed).ConfigureAwait(false);
        if (reason == CodexRefreshReason.Notification)
            Interlocked.CompareExchange(ref _notificationSignal, NewRefreshSignal(), notification);
        return reason;
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var id = Interlocked.Increment(ref _nextRequestId);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, response)) throw new InvalidOperationException("Could not register Codex request.");
        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));
            try { return await response.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
            {
                throw new CodexAppServerException(CodexAppServerFailure.Timeout, $"Codex request timed out: {method}");
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                throw new CodexAppServerException(CodexAppServerFailure.Connection, "Codex App Server stopped.");
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken) =>
        SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new CodexAppServerException(CodexAppServerFailure.Connection, "Codex App Server is not running.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process.HasExited) throw new CodexAppServerException(CodexAppServerFailure.Connection, "Codex App Server exited.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw new CodexAppServerException(CodexAppServerFailure.Connection, "Codex App Server pipe closed.");
        }
        finally { _writeGate.Release(); }
    }

    private async Task ReadStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var idProperty) && TryGetRequestId(idProperty, out var id))
                    {
                        if (_pending.TryGetValue(id, out var pending))
                        {
                            if (root.TryGetProperty("error", out var error))
                            {
                                pending.TrySetException(CreateProtocolException(error));
                            }
                            else if (root.TryGetProperty("result", out var result))
                            {
                                pending.TrySetResult(result.Clone());
                            }
                        }
                        continue;
                    }

                    if (root.TryGetProperty("method", out var method) &&
                        string.Equals(method.GetString(), "account/rateLimits/updated", StringComparison.Ordinal))
                    {
                        _notificationSignal.TrySetResult(CodexRefreshReason.Notification);
                        RateLimitsUpdated?.Invoke();
                    }
                }
                catch (JsonException)
                {
                    // Ignore one malformed unsolicited line; pending requests still have a strict timeout.
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally { SignalExited(); }
    }

    private static async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                // Drain only. Stderr can contain diagnostic data and is intentionally not persisted or exposed to UI.
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is Process process)
            RuntimeDiagnostics.Write($"[Codex] Owned process exited Code={SafeExitCode(process)}");
        SignalExited();
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "unknown"; }
    }

    private void SignalExited()
    {
        _exitSignal.TrySetResult(CodexRefreshReason.ProcessExited);
        foreach (var pending in _pending.Values)
            pending.TrySetException(new CodexAppServerException(CodexAppServerFailure.Connection, "Codex App Server exited."));
        Exited?.Invoke();
    }

    private static bool TryGetRequestId(JsonElement id, out long value)
    {
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out value)) return true;
        if (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out value)) return true;
        value = default;
        return false;
    }

    private static CodexAppServerException CreateProtocolException(JsonElement error)
    {
        var message = error.TryGetProperty("message", out var messageProperty) ? messageProperty.GetString() ?? "Protocol error" : "Protocol error";
        var failure = message.Contains("method", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("experimental", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("initialized", StringComparison.OrdinalIgnoreCase)
            ? CodexAppServerFailure.ProtocolUnsupported : CodexAppServerFailure.Error;
        return new CodexAppServerException(failure, message);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        var process = _process;
        _process = null;
        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            try { process.StandardInput.Close(); } catch (IOException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                if (!process.HasExited) process.Kill(true);
            }
            process.Dispose();
        }
        if (_stdoutTask is not null) await IgnoreFailureAsync(_stdoutTask).ConfigureAwait(false);
        if (_stderrTask is not null) await IgnoreFailureAsync(_stderrTask).ConfigureAwait(false);
        _shutdown.Dispose();
        _writeGate.Dispose();
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static TaskCompletionSource<CodexRefreshReason> NewRefreshSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CodexAppServerClient));
    }
}

public static class CodexRateLimitResponseParser
{
    /// <summary>Allow-lists only the two display windows; unrelated account response fields are discarded immediately.</summary>
    public static CodexRateLimitSnapshot Parse(JsonElement result)
    {
        JsonElement bucket;
        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId) && byLimitId.ValueKind == JsonValueKind.Object)
        {
            if (!byLimitId.TryGetProperty("codex", out bucket))
                bucket = byLimitId.EnumerateObject().Select(property => property.Value).FirstOrDefault();
        }
        else if (!result.TryGetProperty("rateLimits", out bucket))
        {
            return new CodexRateLimitSnapshot(null, null);
        }
        return new CodexRateLimitSnapshot(ParseWindow(bucket, "primary"), ParseWindow(bucket, "secondary"));
    }

    private static CodexRateLimitWindow? ParseWindow(JsonElement bucket, string propertyName)
    {
        if (!bucket.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("windowDurationMins", out var duration) || !duration.TryGetInt32(out var minutes) || minutes <= 0 ||
            !window.TryGetProperty("usedPercent", out var used) || !used.TryGetInt32(out var percent) || percent is < 0 or > 100)
            return null;
        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resetsAt", out var resetsAt) && resetsAt.TryGetInt64(out var epoch))
        {
            try { reset = DateTimeOffset.FromUnixTimeSeconds(epoch); }
            catch (ArgumentOutOfRangeException) { }
        }
        return new CodexRateLimitWindow(minutes, percent, reset);
    }
}
