namespace DevOverlay.Metrics.Windows;

internal enum BatteryPowerSource { Unavailable, Native, Estimated }
internal sealed record BatteryPowerEstimate(double? Watts, string Reason, uint? StartCapacity = null,
    uint? EndCapacity = null, double? ElapsedSeconds = null, string? ResetReason = null);

/// <summary>
/// 같은 절대 단위 배터리의 최근 순 에너지 흐름 평균(부호 있는 W: +는 저장 에너지 증가, -는 감소). Native rate와 혼합하지 않는다.
/// 논리 상태(충전/방전)와 실제 용량 방향은 별개이므로 어느 상태에서든 두 부호 모두 유효하다. 상태 전환은 이력을 reset한다.
/// </summary>
internal sealed class BatteryPowerEstimator
{
    internal const int MinimumSeconds = 15, WindowSeconds = 60, StaleSeconds = 30, MaximumGapSeconds = 10;
    private readonly List<(DateTimeOffset Time, uint Capacity)> _history = [];
    private string? _identity;
    private BatteryFlow _flow;
    private DateTimeOffset? _lastChange;
    private string? _resetReason;
    public BatteryPowerEstimate Last { get; private set; } = new(null, "NoHistory");

    public BatteryPowerEstimate Reset(string reason)
    { _history.Clear(); _identity = null; _lastChange = null; _resetReason = reason; return Last = new(null, reason, ResetReason: reason); }

    public BatteryPowerEstimate Observe(DateTimeOffset now, string identity, BatteryFlow flow, uint capacity)
    {
        if (capacity == uint.MaxValue) return Reset("UnknownCapacity");
        if (flow is not (BatteryFlow.Charging or BatteryFlow.Discharging)) return Reset("InactiveFlow");
        if (_history.Count > 0 && (_identity != identity || _flow != flow)) Reset("IdentityOrFlowChanged");
        _identity = identity; _flow = flow;
        if (_history.Count > 0)
        {
            var previous = _history[^1];
            var gap = (now - previous.Time).TotalSeconds;
            if (gap <= 0 || gap > MaximumGapSeconds)
            { Reset("TimeDiscontinuity"); _identity = identity; _flow = flow; }
            else
            {
                var delta = (double)capacity - previous.Capacity;
                // 논리 상태와 용량 기울기의 부호는 독립이다: 충전 중 감소/방전 중 증가도 유효한 부호 있는 전력이다.
                if (Math.Abs(delta) * 3.6 / gap > 999.9) return Reset("ImplausibleCapacityJump");
                if (delta != 0) _lastChange = now;
            }
        }
        _history.Add((now, capacity));
        _history.RemoveAll(sample => (now - sample.Time).TotalSeconds > WindowSeconds);
        // 호출 폭주에서도 무한 증가하지 않는다. 정상 1~2초 주기에서는 최대 61개.
        if (_history.Count > 128) _history.RemoveRange(0, _history.Count - 128);
        var first = _history[0];
        var seconds = (now - first.Time).TotalSeconds;
        var changes = 0;
        for (var i = 1; i < _history.Count; i++) if (_history[i].Capacity != _history[i - 1].Capacity) changes++;
        var reason = seconds < MinimumSeconds ? "InsufficientInterval" : changes < 2 ? "InsufficientCapacityChanges" :
            _lastChange is null || (now - _lastChange.Value).TotalSeconds >= StaleSeconds ? "StaleCapacity" : null;
        // 부호 있는 기울기. 절댓값으로 방향을 지우지 않는다.
        var watts = ((double)capacity - first.Capacity) * 3.6 / Math.Max(seconds, 1);
        return Last = new(reason is null && watts != 0 && Math.Abs(watts) <= 999.9 ? watts : null,
            reason ?? "Ready", first.Capacity, capacity, seconds, _resetReason);
    }
}
