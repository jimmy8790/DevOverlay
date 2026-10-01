namespace DevOverlay.Metrics;

public enum MetricId
{
    CpuUtilization,
    CpuTemperature,
    CpuPower,
    GpuUtilization,
    GpuTemperature,
    GpuPower,
    GpuVramUsed,
    StorageActivity,
    StorageRead,
    StorageWrite,
    NetworkDownload,
    NetworkUpload,
    NetworkTodayTotal,
    FramesPerSecond,
    OnePercentLow,
    FrameTime,
    Latency,
    CodexUsage,
    ClaudeUsage,
    CodexPrimaryRateLimit,
    CodexSecondaryRateLimit,
    ClaudePrimaryRateLimit,
    ClaudeSecondaryRateLimit,
    ClaudeContextRemaining
}
