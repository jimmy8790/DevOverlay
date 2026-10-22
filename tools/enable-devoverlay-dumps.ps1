<#
.SYNOPSIS
  Shows, enables or removes Windows Error Reporting LocalDumps for DevOverlay.exe ONLY (full user-mode dump).
.DESCRIPTION
  Default run is read-only: it prints the current state and what -Apply would change.
  -Apply / -Remove write to HKLM and therefore require an elevated PowerShell; without elevation nothing is changed.
  Only the per-application key  HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\DevOverlay.exe  is touched.
  The global LocalDumps defaults, other applications and every security setting are left alone.

  PRIVACY: a full dump contains the process memory of DevOverlay (settings, network/usage data it has read, possibly
  tokens that Codex/Claude usage providers held in memory). Keep dumps local; do not share them without reviewing.
  SIZE: expect roughly the working set (about 300-400 MB per dump). DumpCount limits how many are kept.
.EXAMPLE
  .\tools\enable-devoverlay-dumps.ps1                  # show state only
  .\tools\enable-devoverlay-dumps.ps1 -Apply           # elevated: enable full dumps for DevOverlay.exe
  .\tools\enable-devoverlay-dumps.ps1 -Remove          # elevated: remove the DevOverlay.exe override again
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Remove,
    [string]$DumpFolder = '%LOCALAPPDATA%\DevOverlay\Diagnostics\Dumps',
    [int]$DumpCount = 3
)
$ErrorActionPreference = 'Stop'
$parent = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps'
$key = Join-Path $parent 'DevOverlay.exe'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Show-State {
    Write-Output "Elevated: $isAdmin"
    foreach ($k in $parent, $key) {
        if (Test-Path $k) {
            Write-Output "[$k]"
            $props = Get-ItemProperty $k | Select-Object * -ExcludeProperty PS*
            if (($props.PSObject.Properties | Measure-Object).Count -eq 0) { Write-Output '  (exists, no values: Windows defaults apply = mini dump to %LOCALAPPDATA%\CrashDumps, 10 kept)' }
            else { $props.PSObject.Properties | ForEach-Object { Write-Output ("  {0} = {1}" -f $_.Name, $_.Value) } }
        } else { Write-Output "[$k] (absent)" }
    }
}

if ($Apply -and $Remove) { throw 'Use either -Apply or -Remove.' }
if (-not $Apply -and -not $Remove) {
    Show-State
    Write-Output ''
    Write-Output 'Would apply with -Apply (DevOverlay.exe only):'
    Write-Output "  DumpFolder = $DumpFolder   (REG_EXPAND_SZ)"
    Write-Output '  DumpType   = 2             (full dump; the default is 1 = mini, which does not contain the managed heap)'
    Write-Output "  DumpCount  = $DumpCount"
    return
}
if (-not $isAdmin) {
    Write-Output 'Not elevated: nothing was changed. Re-run from an Administrator PowerShell, for example:'
    Write-Output "  Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File ""$PSCommandPath"" $(if ($Apply) { '-Apply' } else { '-Remove' })'"
    exit 1
}
if ($Remove) {
    if (Test-Path $key) { Remove-Item -Path $key -Recurse; Write-Output "Removed $key" } else { Write-Output "$key was not present." }
    Show-State
    return
}
# -Apply
if (-not (Test-Path $parent)) { New-Item -Path $parent -Force | Out-Null }   # normally present; created empty, never given values
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name DumpFolder -Value $DumpFolder -PropertyType ExpandString -Force | Out-Null
New-ItemProperty -Path $key -Name DumpType -Value 2 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name DumpCount -Value $DumpCount -PropertyType DWord -Force | Out-Null
$resolved = [Environment]::ExpandEnvironmentVariables($DumpFolder)
if (-not (Test-Path $resolved)) { New-Item -ItemType Directory -Path $resolved -Force | Out-Null }
Write-Output "Enabled full dumps for DevOverlay.exe -> $resolved (keeps $DumpCount)."
Show-State
