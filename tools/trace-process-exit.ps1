<#
.SYNOPSIS
  DevOverlay와 독립적인, 크기가 제한된 ProcessStart/ProcessStop ETW 진단.
.DESCRIPTION
  Start/Stop만 관리자 권한 필요. -Elevate는 해당 동작에만 명시적으로 UAC를 요청한다.
  앱/서비스/레지스트리를 바꾸지 않는다. 재부팅 후 자동 시작하지 않는다.
  Stop으로 ETL을 닫아 보존한 뒤 Read로 읽는다. 새 Start는 항상 다른 ETL을 만든다.
.EXAMPLE
  .\tools\trace-process-exit.ps1 -Action Start -Elevate
  .\tools\trace-process-exit.ps1 -Action Status -Elevate
  .\tools\trace-process-exit.ps1 -Action Stop -Elevate
  .\tools\trace-process-exit.ps1 -Action Read -Etl '보존한.etl' | Format-List
# 순환 파일은 최대64MB, 메모리 버퍼는 최대16*64KB. 장기 기록은 오래된 이벤트를 덮는다.
# 재부팅 후에는 Start를 다시 실행해야 한다. 이 도구는 앱을 감시/재실행하지 않는다.
#>
[CmdletBinding()]
param(
    [ValidateSet('Status','Start','Stop','Read','SelfTest','VerifyAndArm')][string]$Action = 'Status',
    [string]$TraceDirectory = (Join-Path $env:LOCALAPPDATA 'DevOverlay\Diagnostics\ExitTrace'),
    [string]$Etl,
    [string]$ImageName = 'DevOverlay.exe',
    [uint32]$TargetProcessId = 0,
    [switch]$Elevate
)
$ErrorActionPreference = 'Stop'
$sessionName = 'DevOverlayProcessExit'
$provider = '{22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716}'
$TraceDirectory = [IO.Path]::GetFullPath($TraceDirectory)
$statePath = Join-Path $TraceDirectory 'trace-state.clixml'

function Convert-ExitCode([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    if ($Value.StartsWith('0x', [StringComparison]::OrdinalIgnoreCase)) {
        $number = [Convert]::ToInt64($Value.Substring(2), 16)
    } else { $number = [long]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture) }
    if ($number -lt [int32]::MinValue -or $number -gt [uint32]::MaxValue) { throw 'ExitCode가 32비트 범위를 벗어났습니다.' }
    return [uint32]($number -band 0xffffffffL)
}

function Convert-ProcessEvent([xml]$Xml) {
    if ($Xml.Event.System.Provider.GetAttribute('Guid').Trim('{}') -ine $provider.Trim('{}')) { return }
    $eventId = [int]$Xml.Event.System.EventID.InnerText
    # EventID에 속성이 없는 버전도 처리한다.
    if (-not $eventId) { $eventId = [int][string]$Xml.Event.System.EventID }
    if ($eventId -notin 1,2) { return }
    $data = @{}
    foreach ($node in $Xml.Event.EventData.Data) { $data[$node.GetAttribute('Name')] = $node.InnerText }
    $code = if ($eventId -eq 2) { Convert-ExitCode $data['ExitCode'] } else { $null }
    $category = if ($null -eq $code) { 'Unavailable' } elseif ($code -eq 0) { 'ZeroExit' }
        elseif ($code -eq 0xc0000005L) { 'AccessViolation' } elseif ($code -eq 0xc0000409L) { 'FailFastOrSecurity' }
        else { 'OtherExitCode' }
    [pscustomobject]@{
        Event = $(if ($eventId -eq 1) { 'ProcessStart' } else { 'ProcessStop' })
        Timestamp = $Xml.Event.System.TimeCreated.GetAttribute('SystemTime')
        ProcessId = [uint32]$data['ProcessID']; Image = $data['ImageName']
        CreateTime = $data['CreateTime']; ExitTime = $data['ExitTime']
        RawExitCode = $data['ExitCode']; ExitCodeDecimal = $code
        ExitCodeHex = $(if ($null -ne $code) { '0x{0:X8}' -f $code } else { $null })
        Category = $category; RawData = $data
    }
}

if ($Action -eq 'SelfTest') {
    foreach ($case in @(@('0',0L),@('3221225477',0xc0000005L),@('-1073741819',0xc0000005L),@('0xC0000409',0xc0000409L),@('23',23L))) {
        if ((Convert-ExitCode $case[0]) -ne $case[1]) { throw 'ExitCode 변환 테스트 실패' }
    }
    foreach ($version in 0,2) {
        $fixture = [xml]('<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Guid="'+$provider+'"/><EventID>2</EventID><Version>'+$version+'</Version><TimeCreated SystemTime="2026-10-27T00:00:00Z"/><Execution ProcessID="999"/></System><EventData><Data Name="ImageName">C:\Test\DevOverlay.exe</Data><Data Name="ProcessID">123</Data><Data Name="CreateTime">2026-10-26T23:00:00Z</Data><Data Name="ExitTime">2026-10-27T00:00:00Z</Data><Data Name="ExitCode">3221225477</Data></EventData></Event>')
        $result = Convert-ProcessEvent $fixture
        if ($result.ProcessId -ne 123 -or $result.ExitCodeHex -ne '0xC0000005' -or $result.Category -ne 'AccessViolation') { throw 'ETW 이름 기반 변환 테스트 실패' }
        $fixture.Event.EventData.RemoveChild($fixture.Event.EventData.SelectSingleNode('*[@Name="ExitCode"]')) | Out-Null
        if ($null -ne (Convert-ProcessEvent $fixture).ExitCodeDecimal) { throw '누락 ExitCode가 0으로 바뀌었습니다.' }
    }
    'SelfTest PASS: 32비트 부호/16진수, 이름 기반 payload PID, 버전/순서, 누락 코드'
    return
}

if ($Action -eq 'Read') {
    if (-not $Etl) { throw '-Etl에 닫힌 보존 ETL 경로를 지정하세요.' }
    Get-WinEvent -Path $Etl -Oldest | ForEach-Object {
        $row = Convert-ProcessEvent ([xml]$_.ToXml())
        if ($row -and ($TargetProcessId -eq 0 -or $row.ProcessId -eq $TargetProcessId) -and
            ([IO.Path]::GetFileName($row.Image) -like $ImageName)) { $row }
    }
    return
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Action -eq 'Status' -and (-not $Elevate -or $isAdmin)) {
    $query = & logman query $sessionName -ets
    $query
    $queryCode = $LASTEXITCODE
    if ($queryCode -ne 0) { Write-Warning '실시간 조회 실패: 아래 상태 파일은 마지막 기록일 뿐입니다. 관리자 조회는 -Action Status -Elevate를 사용하세요.' }
    elseif (Test-Path -LiteralPath $TraceDirectory) {
        [pscustomobject]@{ObservedAt=[DateTimeOffset]::Now;ExitCode=$queryCode;Query=$query} |
            Export-Clixml -LiteralPath (Join-Path $TraceDirectory 'trace-status.clixml')
    }
    if (Test-Path -LiteralPath $statePath) { Import-Clixml -LiteralPath $statePath }
    return
}

if (-not $isAdmin) {
    if (-not $Elevate) { throw 'Start/Stop에는 관리자 권한이 필요합니다. 명시적으로 -Elevate를 사용하세요.' }
    $hostExe = (Get-Process -Id $PID).Path
    $child = Start-Process -FilePath $hostExe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
        '-NoProfile','-File',('"'+$PSCommandPath+'"'),'-Action',$Action,'-TraceDirectory',('"'+$TraceDirectory+'"'))
    if ($child.ExitCode -ne 0) { throw "관리자 진단 동작 실패: $($child.ExitCode)" }
    if ($Action -eq 'Status') { Import-Clixml -LiteralPath (Join-Path $TraceDirectory 'trace-status.clixml'); return }
    if (Test-Path -LiteralPath $statePath) { Import-Clixml -LiteralPath $statePath }
    return
}

if ($Action -eq 'VerifyAndArm') {
    # 앱이 아닌 짧은 cmd 두 개로 실제 ProcessStop의 0/비영 코드를 검증한다.
    $checks = foreach ($expected in 0,23) {
        $probe = Start-Process -FilePath $env:ComSpec -WindowStyle Hidden -PassThru -Wait -ArgumentList @('/d','/c',('exit '+$expected))
        [pscustomobject]@{ProcessId=$probe.Id;Expected=$expected}
    }
    Start-Sleep -Seconds 1
    $closed = & $PSCommandPath -Action Stop -TraceDirectory $TraceDirectory
    $closedState = Import-Clixml -LiteralPath $statePath
    $rows = @(& $PSCommandPath -Action Read -Etl $closedState.Etl -ImageName 'cmd.exe')
    foreach ($check in $checks) {
        $stop = @($rows | Where-Object { $_.ProcessId -eq $check.ProcessId -and $_.Event -eq 'ProcessStop' })
        if ($stop.Count -ne 1 -or $stop[0].ExitCodeDecimal -ne $check.Expected) { throw '실제 ETW 종료 코드 검증 실패. 닫힌 ETL은 보존했습니다.' }
        if (-not ($rows | Where-Object { $_.ProcessId -eq $check.ProcessId -and $_.Event -eq 'ProcessStart' })) { throw 'ProcessStart 누락' }
    }
    $rows | Where-Object { $_.ProcessId -in $checks.ProcessId } | Export-Clixml -LiteralPath ($closedState.Etl+'.verified.clixml')
    & $PSCommandPath -Action Start -TraceDirectory $TraceDirectory
    return
}

if ($Action -eq 'Start') {
    $existing = & logman query $sessionName -ets 2>&1
    if ($LASTEXITCODE -eq 0) { throw '기존 세션이 있습니다. 보존/Stop 전에는 새 기록을 시작하지 않습니다.' }
    New-Item -ItemType Directory -Path $TraceDirectory -Force | Out-Null
    $stamp = [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
    $path = Join-Path $TraceDirectory ('process-exit-'+$stamp+'.etl')
    # 다른 WPR/ETW 세션에는 손대지 않으며 시작 전 현황을 별도 보존한다.
    @(& logman query -ets; & wpr -status) | Export-Clixml -LiteralPath (Join-Path $TraceDirectory ('before-'+$stamp+'.clixml'))
    & logman create trace $sessionName -p $provider 0x10 4 -o $path -f bincirc -max 64 -bs 64 -nb 4 16 -ft 00:00:05 -ets
    if ($LASTEXITCODE -ne 0) { throw 'ETW 시작 실패' }
    $status = & logman query $sessionName -ets
    if ($LASTEXITCODE -ne 0) { throw 'ETW 시작 후 상태 확인 실패' }
    $state = [pscustomobject]@{Session=$sessionName;Provider=$provider;Keywords='0x10';Level=4;Started=[DateTimeOffset]::Now;
        Etl=$path;MaxMegabytes=64;FlushSeconds=5;Status='Running';Query=$status}
    $state | Export-Clixml -LiteralPath $statePath
    $state
} else {
    if (-not (Test-Path -LiteralPath $statePath)) { throw '소유 세션 상태 파일이 없어 종료하지 않습니다.' }
    $state = Import-Clixml -LiteralPath $statePath
    if ($state.Session -ne $sessionName) { throw '세션 식별 불일치' }
    & logman stop $sessionName -ets
    if ($LASTEXITCODE -ne 0) { throw 'ETW 종료 실패. 원본을 보존하고 상태를 확인하세요.' }
    $state.Status = 'Stopped'
    $state | Add-Member NoteProperty Stopped ([DateTimeOffset]::Now) -Force
    $state | Export-Clixml -LiteralPath $statePath
    $state
}
