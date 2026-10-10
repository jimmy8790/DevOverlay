#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(5, 900)][int]$DurationSeconds = 180
)

# 제품 소스를 그대로 사용한다. GUI/설정/서비스/배터리 설정은 변경하지 않는다.
$ErrorActionPreference = 'Stop'
$assemblyPath = Join-Path $PSScriptRoot "../bin/$Configuration/net8.0-windows10.0.19041.0/DevOverlay.dll"
if (-not (Test-Path -LiteralPath $assemblyPath)) { throw "먼저 $Configuration 빌드를 수행해 주세요." }
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $assemblyPath).Path)
$source = [Activator]::CreateInstance($assembly.GetType('DevOverlay.Platform.Windows.SystemBatterySource'), $true)
$snapshotsMethod = $assembly.GetType('DevOverlay.Metrics.Windows.SystemBatteryMetricProvider').GetMethod(
    'CreateSnapshots', [Reflection.BindingFlags]'Static,NonPublic')
$formatter = $assembly.GetType('DevOverlay.Presentation.MetricTextFormatter').GetMethod('Format')
$watch = [Diagnostics.Stopwatch]::StartNew()
$previousAc = $null
$previousFlow = $null
$transitionAt = $null
$wasAvailable = $false
$lastPrint = -10.0
$lastState = ''
Write-Host '배터리 원시 값과 제품 판정을 기록합니다. 안내에 따라 충전기를 분리·재연결해 주세요. Ctrl+C로 종료할 수 있습니다.'
try {
    while ($watch.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $reading = $source.Read()
        $raw = $source.LastNativeReading
        $ac = $raw.PowerStatus.AcLineStatus
        $flow = $reading.Flow.ToString()
        $transition = ($null -ne $previousAc -and $ac -ne $previousAc) -or
            ($null -ne $previousFlow -and $flow -ne $previousFlow)
        if ($transition) { $transitionAt = $watch.Elapsed.TotalSeconds; $wasAvailable = $false }
        $available = $null -ne $reading.Watts
        $becameAvailable = $available -and -not $wasAvailable
        $state = "$ac/$flow/$available/$($source.RefreshInterval.TotalSeconds)"
        if ($transition -or $becameAvailable -or $state -ne $lastState -or $watch.Elapsed.TotalSeconds - $lastPrint -ge 10) {
            $snapshots = $snapshotsMethod.Invoke($null, @($reading))
            $texts = @($snapshots | ForEach-Object { $formatter.Invoke($null, @($_.PSObject.BaseObject)) })
            [pscustomobject]@{
                Time = [DateTimeOffset]::UtcNow.ToString('o')
                ElapsedSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 2)
                AC = $ac; Flow = $flow; Percentage = $reading.Percentage
                NativeDevices = @($raw.Devices | ForEach-Object {
                    [pscustomobject]@{ Capabilities = $_.Capabilities; FullCapacity = $_.FullCapacity
                        StatusPresent = $null -ne $_.Status; PowerState = $_.Status.PowerState
                        Capacity = $_.Status.Capacity; Rate = $_.Status.Rate }
                })
                Win32Errors = @($source.LastDeviceErrors)
                Watts = $reading.Watts; RemainingSeconds = $reading.RemainingSeconds
                PowerSource = $reading.PowerSource.ToString(); Estimate = $source.LastEstimate
                LeftSource = $reading.LeftSource.ToString(); WindowsLifeSeconds = $raw.PowerStatus.LifeSeconds
                TimeToFullSeconds = $reading.TimeToFullSeconds; FullSource = $reading.FullSource.ToString()
                PollSeconds = $source.RefreshInterval.TotalSeconds
                Transition = $transition
                RecoverySeconds = $(if ($becameAvailable -and $null -ne $transitionAt) { [math]::Round($watch.Elapsed.TotalSeconds - $transitionAt, 2) } else { $null })
                HUD = 'BAT ' + ($texts -join ' ').Trim()
            } | ConvertTo-Json -Depth 5 -Compress
            $lastPrint = $watch.Elapsed.TotalSeconds; $lastState = $state
        }
        $previousAc = $ac; $previousFlow = $flow; $wasAvailable = $available
        Start-Sleep -Milliseconds ([int]($source.RefreshInterval.TotalMilliseconds))
    }
} finally { $source.Dispose() }
