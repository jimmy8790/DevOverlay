<#
.SYNOPSIS
  Read-only timeline of everything that may explain a DevOverlay crash: Windows crash events, driver / PresentMon /
  power / remote-session / display events in a narrow window, DevOverlay's own lifecycle log and session marker, and dumps.
.DESCRIPTION
  Never modifies event logs, the registry or any DevOverlay file. By default it analyses the newest DevOverlay crash
  (Application Error 1000). Use -CrashTime to analyse an earlier one. Run it from any prompt (no elevation needed, but
  some channels such as Kernel-PnP may need it and are then reported as unreadable).
.EXAMPLE
  .\tools\collect-crash-evidence.ps1
  .\tools\collect-crash-evidence.ps1 -CrashTime '2026-10-22 13:34:29' -MinutesBefore 10 -OutFile $env:TEMP\crash.txt
#>
[CmdletBinding()]
param(
    [datetime]$CrashTime,
    [int]$MinutesBefore = 5,
    [int]$MinutesAfter = 1,
    [string]$OutFile
)
$ErrorActionPreference = 'Continue'
$diag = Join-Path $env:LOCALAPPDATA 'DevOverlay\Diagnostics'

function Get-Events([string]$Log, [hashtable]$Filter) {
    try { Get-WinEvent -FilterHashtable (@{ LogName = $Log } + $Filter) -ErrorAction Stop }
    catch [System.Exception] {
        if ($_.FullyQualifiedErrorId -notmatch 'NoMatchingEventsFound') { Write-Warning "Cannot read '$Log': $($_.Exception.Message)" }
    }
}

if (-not $CrashTime) {
    $latest = Get-Events 'Application' @{ Id = 1000 } | Where-Object { $_.Message -match 'DevOverlay' } | Sort-Object TimeCreated -Descending | Select-Object -First 1
    if (-not $latest) { Write-Output 'No DevOverlay Application Error (1000) found; pass -CrashTime.'; return }
    $CrashTime = $latest.TimeCreated
}
$start = $CrashTime.AddMinutes(-$MinutesBefore); $end = $CrashTime.AddMinutes($MinutesAfter)
$lines = [System.Collections.Generic.List[string]]::new()
function Add-Line([string]$text) { $lines.Add($text) }

Add-Line "DevOverlay crash evidence  (read-only)"
Add-Line "Crash time : $($CrashTime.ToString('yyyy-MM-dd HH:mm:ss'))   Window: $($start.ToString('HH:mm:ss')) .. $($end.ToString('HH:mm:ss'))"
Add-Line ''

# ---- 1. The crash itself -------------------------------------------------------------------------------------------
Add-Line '== 1. Crash events (Application log) =='
$crash = Get-Events 'Application' @{ Id = 1000, 1001, 1026; StartTime = $start; EndTime = $end } |
    Where-Object { $_.Message -match 'DevOverlay|APPCRASH|LiveKernelEvent' } | Sort-Object TimeCreated
foreach ($e in $crash) {
    $first = ($e.Message -split "`r?`n" | Select-Object -First 1)
    Add-Line ('{0}  id={1,-5} {2}  {3}' -f $e.TimeCreated.ToString('HH:mm:ss'), $e.Id, $e.ProviderName, $first)
    if ($e.Id -eq 1026) { ($e.Message -split "`r?`n" | Where-Object { $_ -match '^(Exception Info:|\s+at )' } | Select-Object -First 12) | ForEach-Object { Add-Line "      $($_.Trim())" } }
    if ($e.Id -eq 1000) { ($e.Message -split "`r?`n" | Where-Object { $_ -match 'Faulting|Exception code|Fault offset' }) | ForEach-Object { Add-Line "      $($_.Trim())" } }
}
if (-not $crash) { Add-Line '(none in window)' }
Add-Line ''

# ---- 2. Correlated system events -----------------------------------------------------------------------------------
Add-Line '== 2. Correlated events (System / Application / operational channels) =='
$categories = @(
    @{ Name = 'GPU/display driver'; Log = 'System'; Match = 'nvlddmkm|NVIDIA|NVDisplay|Display|dxgkrnl|igfx|Arc|LiveKernelEvent|WATCHDOG|TDR|GPU' ; Ids = $null },
    @{ Name = 'PresentMon / services'; Log = 'System'; Match = 'PresentMon|Service Control Manager'; Ids = $null },
    @{ Name = 'Power / suspend'; Log = 'System'; Match = 'Kernel-Power|Power-Troubleshooter|Kernel-Boot|Kernel-General'; Ids = $null },
    @{ Name = 'PnP / device changes'; Log = 'System'; Match = 'UserPnp|Kernel-PnP|DriverFrameworks'; Ids = $null },
    @{ Name = 'Remote / session'; Log = 'System'; Match = 'TerminalServices|chromoting|RemoteDesktop|Winlogon|User32|Microsoft-Windows-Dwm'; Ids = $null },
    @{ Name = 'Reporting'; Log = 'Application'; Match = 'Windows Error Reporting|Application Hang|Application Error|\.NET Runtime|PresentMon|DevOverlay|chromoting|Desktop Window Manager'; Ids = $null }
)
$extra = @(
    @{ Log = 'Microsoft-Windows-TerminalServices-LocalSessionManager/Operational' },
    @{ Log = 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational' },
    @{ Log = 'Microsoft-Windows-Kernel-PnP/Configuration' }
)
$timeline = [System.Collections.Generic.List[object]]::new()
foreach ($c in $categories) {
    foreach ($e in (Get-Events $c.Log @{ StartTime = $start; EndTime = $end })) {
        if ($e.ProviderName -match $c.Match -or ($e.Message -match $c.Match)) {
            if ($e.Id -in 1000, 1026) { continue } # already shown above
            $timeline.Add([pscustomobject]@{ Time = $e.TimeCreated; Category = $c.Name; Log = $c.Log; Id = $e.Id; Provider = $e.ProviderName; Text = (($e.Message -split "`r?`n" | Select-Object -First 1) -replace '\s+', ' ') })
        }
    }
}
foreach ($x in $extra) {
    foreach ($e in (Get-Events $x.Log @{ StartTime = $start; EndTime = $end })) {
        $timeline.Add([pscustomobject]@{ Time = $e.TimeCreated; Category = 'Remote / session / PnP'; Log = $x.Log; Id = $e.Id; Provider = $e.ProviderName; Text = (($e.Message -split "`r?`n" | Select-Object -First 1) -replace '\s+', ' ') })
    }
}
# Windows Error Reporting can re-report dozens of old LiveKernelEvent buckets right after a crash; collapse identical entries.
$groups = $timeline | Sort-Object Time | Group-Object { "$($_.Id)|$($_.Provider)|$($_.Text)" }
foreach ($g in ($groups | Sort-Object { $_.Group[0].Time })) {
    $t = $g.Group[0]; $last = $g.Group[-1]
    $text = if ($t.Text.Length -gt 150) { $t.Text.Substring(0, 150) + '...' } else { $t.Text }
    $delta = [int][math]::Round(($t.Time - $CrashTime).TotalSeconds)
    $offset = if ($delta -ge 0) { "T+$delta" } else { "T$delta" } # seconds relative to the crash
    $repeat = if ($g.Count -gt 1) { "  [x$($g.Count), last $($last.Time.ToString('HH:mm:ss'))]" } else { '' }
    Add-Line ('{0}  ({1,-7}s)  {2,-22} id={3,-5} {4}  {5}{6}' -f $t.Time.ToString('HH:mm:ss'), $offset, $t.Category, $t.Id, $t.Provider, $text, $repeat)
}
if ($timeline.Count -eq 0) { Add-Line '(no correlated events in window)' }
Add-Line ''

# ---- 3. DevOverlay's own records -----------------------------------------------------------------------------------
Add-Line '== 3. DevOverlay lifecycle log (same window) and session marker =='
foreach ($name in 'lifecycle.log.old', 'lifecycle.log') {
    $path = Join-Path $diag $name
    if (-not (Test-Path $path)) { continue }
    foreach ($line in Get-Content $path -ErrorAction SilentlyContinue) {
        if ($line -match '^(\d{4}-\d\d-\d\dT[\d:.]+[+-]\d\d:\d\d)') {
            $t = [datetimeoffset]::Parse($Matches[1]).LocalDateTime
            if ($t -ge $start -and $t -le $end.AddMinutes(30)) { Add-Line "$name  $line" }
        }
    }
}
$marker = Join-Path $diag 'session.marker'
if (Test-Path $marker) { Add-Line 'session.marker (still present => the process that wrote it did not exit cleanly, or is still running):'; Get-Content $marker | ForEach-Object { Add-Line "    $_" } }
else { Add-Line 'session.marker: absent (clean exit, or this build predates the marker).' }
Add-Line ''

# ---- 4. Dumps ------------------------------------------------------------------------------------------------------
Add-Line '== 4. Dump files near the crash (WER LocalDumps / CrashDumps) =='
$dumpDirs = @((Join-Path $diag 'Dumps'), (Join-Path $env:LOCALAPPDATA 'CrashDumps'))
$found = foreach ($d in $dumpDirs) { if (Test-Path $d) { Get-ChildItem $d -Filter *.dmp -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'DevOverlay' -and $_.LastWriteTime -ge $start -and $_.LastWriteTime -le $end.AddMinutes(10) } } }
foreach ($f in $found) { Add-Line ('{0}  {1:N0} MB  {2}' -f $f.LastWriteTime.ToString('HH:mm:ss'), ($f.Length / 1MB), $f.FullName) }
if (-not $found) { Add-Line '(no DevOverlay dump in window)' }
Add-Line ''

# ---- 5. Environment snapshot ---------------------------------------------------------------------------------------
Add-Line '== 5. Environment =='
$gpu = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name) driver=$($_.DriverVersion) date=$($_.DriverDate.ToString('yyyy-MM-dd'))" }
$gpu | ForEach-Object { Add-Line "GPU: $_" }
$pm = Join-Path $env:ProgramFiles 'Intel\PresentMonSharedService\PresentMonAPI2.dll'
if (Test-Path $pm) { Add-Line "PresentMonAPI2.dll: $((Get-Item $pm).VersionInfo.FileVersion)" }
Get-Service -Name '*PresentMon*' -ErrorAction SilentlyContinue | ForEach-Object { Add-Line "Service $($_.Name): $($_.Status)" }
$nv = Join-Path $env:SystemRoot 'System32\nvml.dll'
if (Test-Path $nv) { Add-Line "nvml.dll: $((Get-Item $nv).VersionInfo.FileVersion)" }
Get-CimInstance Win32_Process -Filter "Name='DevOverlay.exe'" -ErrorAction SilentlyContinue | ForEach-Object { Add-Line "Running DevOverlay.exe pid=$($_.ProcessId) path=$($_.ExecutablePath)" }

if ($OutFile) { $lines | Set-Content -Path $OutFile -Encoding UTF8; Write-Output "Report written to $OutFile" }
$lines
