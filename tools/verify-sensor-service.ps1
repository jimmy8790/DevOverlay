<#
.SYNOPSIS
  Read-only check of an installed DevOverlay Sensor Service. Run from a NON-elevated PowerShell after Install/Repair.
.DESCRIPTION
  PASS requires: the SCM ImagePath is the quoted protected copy under %ProgramFiles%\DevOverlaySensorService\current,
  every object there is owned by SYSTEM/Administrators/TrustedInstaller with no modifying rights for anyone else,
  and a non-elevated token cannot create, rename, or overwrite files there.
#>
$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()
$serviceName = 'DevOverlaySensorService'
$root = Join-Path $env:ProgramFiles 'DevOverlaySensorService'
$current = Join-Path $root 'current'
$expected = '"' + (Join-Path $current 'DevOverlay.SensorService.exe') + '"'
$trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
# WRITE_DATA, APPEND_DATA, WRITE_EA, DELETE_CHILD, WRITE_ATTRIBUTES, DELETE, WRITE_DAC, WRITE_OWNER, GENERIC_ALL, GENERIC_WRITE
$modifying = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000

$key = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -ErrorAction SilentlyContinue
if (-not $key) { $failures.Add('service is not installed') }
else {
    "ImagePath : $($key.ImagePath)"
    "Account   : $($key.ObjectName)"
    "StartType : $($key.Start) (2 = automatic, 4 = disabled)"
    if ($key.ImagePath -ne $expected) { $failures.Add("ImagePath is not the protected copy (expected $expected)") }
    "State     : $((Get-Service $serviceName).Status)"
}

# -LiteralPath plus an existence check: Windows PowerShell 5.1 treats a missing -Recurse path as a filter over its parent.
$objects = @()
if (Test-Path -LiteralPath $root -PathType Container) {
    $objects = @(Get-Item -LiteralPath $root -Force) + @(Get-ChildItem -LiteralPath $root -Recurse -Force)
}
if ($objects.Count -le 1) { $failures.Add("$root is missing or empty") }
$unsafe = 0
foreach ($item in $objects) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { $failures.Add("reparse point: $($item.FullName)"); continue }
    $acl = Get-Acl -LiteralPath $item.FullName
    $owner = (New-Object Security.Principal.NTAccount($acl.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value
    if ($trusted -notcontains $owner) { $failures.Add("untrusted owner $($acl.Owner): $($item.FullName)"); $unsafe++ }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow' -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)) { continue }
        if (([int]$rule.FileSystemRights -band $modifying) -and $trusted -notcontains $rule.IdentityReference.Value) {
            $failures.Add("$($rule.IdentityReference) can modify $($item.FullName) ($($rule.FileSystemRights))"); $unsafe++
        }
    }
}
"Checked   : $($objects.Count) objects under $root, $unsafe unsafe"
if ($objects.Count) { "Root ACL  :"; (Get-Acl -LiteralPath $root).Access | Format-Table IdentityReference, FileSystemRights, IsInherited -AutoSize | Out-String }

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($elevated) { $failures.Add('run this check from a NON-elevated PowerShell so the write probes are meaningful') }
elseif (Test-Path $current) {
    $probe = Join-Path $current "write-probe-$PID.tmp"
    try { [IO.File]::WriteAllText($probe, 'x'); Remove-Item $probe -Force; $failures.Add('non-elevated token could create a file in the service directory') }
    catch [UnauthorizedAccessException] { 'Probe     : create file denied (expected)' }
    $exe = Join-Path $current 'DevOverlay.SensorService.exe'
    try { [IO.File]::Move($exe, "$exe.probe"); [IO.File]::Move("$exe.probe", $exe); $failures.Add('non-elevated token could rename the service executable') }
    catch [UnauthorizedAccessException] { 'Probe     : rename service exe denied (expected)' }
    catch [IO.IOException] { 'Probe     : rename service exe blocked (file in use or denied)' }
    try { $s = [IO.File]::Open($exe, 'Open', 'Write'); $s.Dispose(); $failures.Add('non-elevated token could open the service executable for writing') }
    catch [UnauthorizedAccessException] { 'Probe     : open exe for write denied (expected)' }
    catch [IO.IOException] { 'Probe     : open exe for write blocked (file in use)' }
}

if ($failures.Count) { ''; 'RESULT: FAIL'; $failures | ForEach-Object { " - $_" }; exit 1 }
''; 'RESULT: PASS'
