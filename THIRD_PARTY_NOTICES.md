# Third-Party Notices

DevOverlay v0.1.1 (win-x64) redistributes the third-party components below, unmodified, as part of the
self-contained release package. Each entry was checked against the exact restored NuGet package
(`.nuspec` license field and any license file inside the `.nupkg`). Where the package itself carries no license text,
the license referenced by the package metadata was retrieved and is included verbatim in `licenses\`.

Third-party components remain under their own licenses; they are not covered by, or relicensed under, DevOverlay's own MIT license (see `LICENSE`).

## Package layout

The release is built as self-contained single-file executables. The managed components listed below are embedded, unmodified, in
`DevOverlay.exe` and in `SensorService\DevOverlay.SensorService.exe`; the "Shipped files" column names the original assembly files.
The WPF native libraries (`*_cor3.dll`) and the Mono helper DLLs are embedded in `DevOverlay.exe` and are extracted unmodified to
`%TEMP%\.net\DevOverlay\` the first time it runs. `MonoPosixHelper.dll` and `libMonoPosixHelper.dll` also remain as normal files next to
`DevOverlay.SensorService.exe`. This layout does not change any license terms; all license texts remain as normal files in this package.

## Redistributed components

| Component | Version | License | Shipped files | License text in package |
| --- | --- | --- | --- | --- |
| .NET runtime (Microsoft.NETCore.App) | 8.0.31 | MIT | runtime files in the root and in `SensorService\` | `licenses\dotnet-runtime-LICENSE.txt`, `licenses\dotnet-runtime-THIRD-PARTY-NOTICES.txt` |
| Windows Desktop runtime (Microsoft.WindowsDesktop.App, incl. WPF native `*_cor3.dll`) | 8.0.31 | MIT | WPF/WinForms runtime files in the root | `licenses\windowsdesktop-runtime-LICENSE.txt` |
| System.Management, System.IO.Ports, System.Diagnostics.PerformanceCounter, System.ServiceProcess.ServiceController, System.Diagnostics.EventLog and other System.* packages | 8.0.0 - 10.0.3 | MIT | `System.*.dll` | covered by the .NET runtime license files above |
| Microsoft.Windows.SDK.NET.Ref (C#/WinRT projection) | 10.0.19041.56 | Microsoft Windows SDK license terms (https://aka.ms/WinSDKLicenseURL) | `WinRT.Runtime.dll`, `Microsoft.Windows.SDK.NET.dll` | see note below |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | `LibreHardwareMonitorLib.dll` (root and `SensorService\`) | `licenses\MPL-2.0.txt` |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | `DiskInfoToolkit.dll` | `licenses\MPL-2.0.txt` |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | `RAMSPDToolkit-NDD.dll` | `licenses\MPL-2.0.txt` |
| BlackSharp.Core | 1.0.4 | MPL-2.0 | `BlackSharp.Core.dll` | `licenses\MPL-2.0.txt` |
| HidSharp | 2.6.4 | Apache-2.0 (package file `LICENSE.txt`, Copyright 2010-2025 James F. Bellinger) | `HidSharp.dll` | `licenses\HidSharp-LICENSE.txt` |
| Mono.Posix.NETStandard | 1.0.0 | MIT (Mono license, see below) | `Mono.Posix.NETStandard.dll`, `MonoPosixHelper.dll`, `libMonoPosixHelper.dll` | `licenses\Mono-LICENSE.txt`, `licenses\zlib-minizip-NOTICE.txt` |

### MPL-2.0 source code availability

The MPL-2.0 components are distributed only in executable form and are unmodified. Their source code is available at the
exact commits recorded in each package's `.nuspec` `<repository>` element:

- LibreHardwareMonitorLib 0.9.6: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor (commit `3d331e3370efb858411f19511373eff65a218701`)
- DiskInfoToolkit 1.1.2: https://github.com/Blacktempel/DiskInfoToolkit (commit `25319eae5781e75bcf141e844ceab2afe94d40ea`)
- RAMSPDToolkit-NDD 1.4.2: https://github.com/Blacktempel/RAMSPDToolkit (commit `3b47b960e0830fef344624ad5e389675d5f0a1ce`)
- BlackSharp.Core 1.0.4: https://github.com/Blacktempel/BlackSharp (commit `1aded1badeba275f0eab429d2a1959391d04de92`)

The full Mozilla Public License 2.0 text is in `licenses\MPL-2.0.txt`.

### Mono.Posix.NETStandard 1.0.0

- The `.nupkg` contains no license file. Its `.nuspec` has no license expression, only
  `licenseUrl` `https://go.microsoft.com/fwlink/?linkid=869050`, which redirects to
  `https://github.com/mono/mono/blob/master/LICENSE`. That file states that "the runtime and its class libraries are
  licensed under the terms of the MIT license" and is included verbatim as `licenses\Mono-LICENSE.txt`.
- The native helpers `MonoPosixHelper.dll` and `libMonoPosixHelper.dll` statically contain zlib 1.2.5
  (Jean-loup Gailly and Mark Adler) and MiniZip 1.01 (Gilles Vollant); their notices are in `licenses\zlib-minizip-NOTICE.txt`.

### Microsoft.Windows.SDK.NET.Ref

`WinRT.Runtime.dll` and `Microsoft.Windows.SDK.NET.dll` come from the `Microsoft.Windows.SDK.NET.Ref` package
(`requireLicenseAcceptance=true`, license URL https://aka.ms/WinSDKLicenseURL). The Windows SDK REDIST list
(https://learn.microsoft.com/en-us/legal/windows-sdk/redist, section "Microsoft.Windows.SDK.NET.Ref") permits distributing these
files unmodified as part of a program to enable it to call WinRT APIs; DevOverlay uses WinRT networking APIs. The Windows SDK
license's distribution requirements apply to these files.

## Not redistributed (installed or downloaded separately)

- **Intel PresentMon Service / PresentMonAPI2.dll v2.6.0** is not bundled. FPS, 1% Low, Frame Time and Render Latency need it
  installed separately (Settings has a "Get PresentMon installer" button that opens the official v2.6.0 release page,
  https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0). DevOverlay loads `PresentMonAPI2.dll` from
  `%ProgramFiles%\Intel\PresentMonSharedService` at runtime.
- **PawnIO** (kernel driver used by LibreHardwareMonitor for CPU package sensors) is not bundled. On the user's explicit request in Settings,
  DevOverlay downloads `PawnIO_setup.exe` from the LibreHardwareMonitor v0.9.6 repository, verifies its pinned SHA-256, and runs it with
  administrator approval.
- **NVIDIA NVML (`nvml.dll`)** is loaded from the user's installed NVIDIA driver and is not bundled.
- **Codex CLI** and **Claude Code** are user-installed tools that DevOverlay invokes locally; they are not bundled.
