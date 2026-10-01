<#
.SYNOPSIS
  Builds the win-x64 self-contained release package under release/ (never committed).
.NOTES
  Layout: one self-contained single-file DevOverlay.exe at the package root (WPF natives are self-extracted to %TEMP%\.net at run time),
  and SensorService\ with one self-contained single-file DevOverlay.SensorService.exe plus its two native helpers
  (not self-extracted: the LocalSystem service must only load code from its protected install folder).
  The package must be built in a scratch -OutputRoot unless it is the real release: the default release\ folder is replaced.
  The unused SensorHost prototype, PDBs and diagnostics are not shipped; PDBs go to a separate symbols ZIP.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $root '..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'release' }
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.VersionPrefix
if (-not $version) { throw 'VersionPrefix missing in Directory.Build.props' }
$name = "DevOverlay-v$version-win-x64"
$out = [IO.Path]::GetFullPath($OutputRoot)
$stage = Join-Path $out "stage\$name"
$zip = Join-Path $out "$name.zip"
$symStage = Join-Path $out "stage\$name-symbols"
$symZip = Join-Path $out "$name-symbols.zip"

if (Test-Path (Join-Path $out 'stage')) { Remove-Item (Join-Path $out 'stage') -Recurse -Force }
Remove-Item $zip, $symZip, "$zip.sha256" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage, $symStage | Out-Null

Push-Location $repo
try {
    # Single-file/self-contained settings live in the project files and apply whenever a RuntimeIdentifier is given.
    dotnet publish DevOverlay.csproj -c Release -r win-x64 "-p:PathMap=$repo\=/_/" -o $stage
    if ($LASTEXITCODE) { throw 'publish DevOverlay failed' }
    dotnet publish DevOverlay.SensorService\DevOverlay.SensorService.csproj -c Release -r win-x64 "-p:PathMap=$repo\=/_/" -o (Join-Path $stage 'SensorService')
    if ($LASTEXITCODE) { throw 'publish SensorService failed' }
}
finally { Pop-Location }

# Symbols travel separately.
foreach ($pdb in Get-ChildItem $stage -Recurse -Filter *.pdb) {
    $rel = $pdb.FullName.Substring($stage.Length).TrimStart('\')
    $dest = Join-Path $symStage $rel
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Move-Item $pdb.FullName $dest
}

# User-facing documents and verified third-party license texts.
Copy-Item (Join-Path $repo 'README.md') $stage
Copy-Item (Join-Path $repo 'LICENSE') $stage
Copy-Item (Join-Path $repo 'THIRD_PARTY_NOTICES.md') $stage
$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
# Use the exact runtime versions the publish embedded, not whatever is newest in the NuGet cache.
# A single-file publish has no loose runtimeconfig/deps.json, so read them from the build output the bundle was made from.
$buildOut = Join-Path $repo 'bin\Release\net8.0-windows10.0.19041.0\win-x64'
$frameworks = (Get-Content (Join-Path $buildOut 'DevOverlay.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
$coreVersion = ($frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
$desktopVersion = ($frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' }).version
if (-not $coreVersion -or -not $desktopVersion) { throw 'Could not read the published runtime versions.' }
$rtDir = Join-Path $nuget "microsoft.netcore.app.runtime.win-x64\$coreVersion"
$desktopDir = Join-Path $nuget "microsoft.windowsdesktop.app.runtime.win-x64\$desktopVersion"
Copy-Item (Join-Path $rtDir 'LICENSE.TXT') (Join-Path $licenses 'dotnet-runtime-LICENSE.txt')
Copy-Item (Join-Path $rtDir 'THIRD-PARTY-NOTICES.TXT') (Join-Path $licenses 'dotnet-runtime-THIRD-PARTY-NOTICES.txt')
Copy-Item (Join-Path $desktopDir 'LICENSE') (Join-Path $licenses 'windowsdesktop-runtime-LICENSE.txt')
$hidVersion = ((Get-Content (Join-Path $buildOut 'DevOverlay.deps.json') -Raw | ConvertFrom-Json).libraries.PSObject.Properties.Name |
    Where-Object { $_ -like 'HidSharp/*' } | Select-Object -First 1) -replace '^HidSharp/', ''
if (-not $hidVersion) { throw 'HidSharp version not found in deps.json.' }
Copy-Item (Join-Path $nuget "hidsharp\$hidVersion\LICENSE.txt") (Join-Path $licenses 'HidSharp-LICENSE.txt')
# License texts that the NuGet packages reference but do not contain (verified copies kept in the repository).
Copy-Item (Join-Path $repo 'licenses\*.txt') $licenses

$forbidden = Get-ChildItem $stage -Recurse -File | Where-Object {
    $_.Extension -in '.pdb', '.trx', '.log', '.csv', '.py', '.cs', '.ps1' -or $_.Name -match 'Tests|xunit|SensorHost'
}
if ($forbidden) { throw "Development files in package: $($forbidden.FullName -join ', ')" }
# Exact layout: any extra loose file (for example a leaked helper exe or a stray DLL) fails the build instead of shipping.
$allowed = 'DevOverlay.exe', 'LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md',
    'SensorService\DevOverlay.SensorService.exe', 'SensorService\MonoPosixHelper.dll', 'SensorService\libMonoPosixHelper.dll',
    'licenses\dotnet-runtime-LICENSE.txt', 'licenses\dotnet-runtime-THIRD-PARTY-NOTICES.txt', 'licenses\HidSharp-LICENSE.txt',
    'licenses\Mono-LICENSE.txt', 'licenses\MPL-2.0.txt', 'licenses\windowsdesktop-runtime-LICENSE.txt', 'licenses\zlib-minizip-NOTICE.txt'
$unexpected = Get-ChildItem $stage -Recurse -File | ForEach-Object { $_.FullName.Substring($stage.Length).TrimStart('\') } | Where-Object { $_ -notin $allowed }
if ($unexpected) { throw "Unexpected files in package: $($unexpected -join ', ')" }
foreach ($required in 'DevOverlay.exe', 'SensorService\DevOverlay.SensorService.exe', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
        'licenses\Mono-LICENSE.txt', 'licenses\MPL-2.0.txt', 'licenses\zlib-minizip-NOTICE.txt') {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "Required package file missing: $required" }
}

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $symStage '*') -DestinationPath $symZip -CompressionLevel Optimal
foreach ($f in $zip, $symZip) {
    $hash = (Get-FileHash $f -Algorithm SHA256).Hash
    "$hash *$(Split-Path $f -Leaf)" | Set-Content "$f.sha256" -Encoding ascii
}
$files = Get-ChildItem $stage -Recurse -File
"{0}: {1:N1} MB zip, {2:N1} MB unpacked, {3} files" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB), (($files | Measure-Object Length -Sum).Sum / 1MB), $files.Count
