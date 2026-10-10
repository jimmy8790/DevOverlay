[CmdletBinding()]
param([string]$LogPath = (Join-Path $PSScriptRoot 'bin/Release/net8.0-windows10.0.19041.0/battery-power-comparison.log'))
$ErrorActionPreference = 'Stop'
$rows = @(Get-Content -LiteralPath $LogPath | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object Kind -eq Sample)
$starts = @(for ($i = 0; $i -lt $rows.Count; $i++) { if ($rows[$i].Transition) { $i } })
for ($stageIndex = 0; $stageIndex -lt $starts.Count; $stageIndex++) {
    $start = $starts[$stageIndex]
    $end = if ($stageIndex + 1 -lt $starts.Count) { $starts[$stageIndex + 1] - 1 } else { $rows.Count - 1 }
    $stage = @($rows[$start..$end])
    Write-Output "전환: $($stage[0].Time), AC=$($stage[0].AC), 관찰=$($stage[-1].SinceTransitionSeconds)초"
    foreach ($source in @('IOCTL', 'SystemBatteryState', 'WinRT')) {
        $first = $stage | Where-Object { $null -ne $_.$source.Watts } | Select-Object -First 1
        $unknown = $stage | Where-Object { $null -eq $_.$source.Watts } | Select-Object -First 1
        $recovered = if ($null -ne $unknown) {
            $stage | Where-Object { $_.ElapsedSeconds -gt $unknown.ElapsedSeconds -and $null -ne $_.$source.Watts } | Select-Object -First 1
        } else { $null }
        $checkpoints = @{}
        foreach ($second in @(5, 30, 60, 90)) {
            $sample = $stage | Where-Object { $_.SinceTransitionSeconds -ge $second } | Select-Object -First 1
            $checkpoints["${second}s"] = if ($null -ne $sample) {
                @{ ActualSeconds = $sample.SinceTransitionSeconds; Watts = $sample.$source.Watts }
            } else { $null }
        }
        [pscustomobject]@{ Source = $source; FirstNumericSeconds = $first.SinceTransitionSeconds
            FirstUnknownSeconds = $unknown.SinceTransitionSeconds
            RecoveryAfterUnknownSeconds = $recovered.SinceTransitionSeconds
            RecoveryWatts = $recovered.$source.Watts; Checkpoints = $checkpoints } | ConvertTo-Json -Depth 5 -Compress
    }
}
