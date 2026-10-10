# 배터리 전력 다중 소스 진단

제품 동작과 분리된 개발 도구다. 기존 IOCTL 구현은 링크하여 재사용하고, Windows 집계/WinRT API는 이 실행 파일에서만 조회한다. WPF·설정·서비스·전원 정책·드라이버를 변경하지 않는다. 관리자 권한 불필요. 추가 PackageReference 없음.

1. PowerShell에서 빌드한다.

   ```powershell
   dotnet build tools/BatteryPowerProbe/BatteryPowerProbe.csproj -c Release -warnaserror
   ```

2. 충전 상태에서 시작하고 여러 유효 샘플을 확인한다. 실행시간(초)과 선택적 로그 경로를 지정할 수 있다. 기본 로그는 도구의 빌드 폴더에 있으며 실행할 때 덮어쓴다. 이전 기록을 보존하려면 다른 경로를 지정한다.

   ```powershell
   .\tools\BatteryPowerProbe\bin\Release\net8.0-windows10.0.19041.0\BatteryPowerProbe.exe 600
   ```

3. 충전기를 분리하고 최소 90초 기록한 뒤 재연결하여 다시 최소 90초 기록한다. 전환 중 프로세스를 재시작하지 않는다. Ctrl+C로 정상 종료하고 장치 핸들을 해제할 수 있다.

4. 원시 기록을 비교한다. 모든 샘플은 로그에 저장하며 콘솔에는 전환/첫 유효값/가용성 변화/10초 간격만 출력한다. API는 약 1초마다 순차 조회하므로 정확히 같은 순간은 아니며 `QueryMilliseconds`로 한 묶음의 시간차를 확인한다.

## 계약과 제한

| 경로 | 실제 계약 | 값/범위 |
|---|---|---|
| 기존 IOCTL | `IOCTL_BATTERY_QUERY_STATUS`, `BATTERY_STATUS.Rate` | 절대 단위 장치 mW, 양수 충전/음수 방전, `int.MinValue` unknown. relative 장치는 W로 환산하지 않음. 기존 복수 배터리 정책 재사용 |
| Windows 집계 | `powrprof!CallNtPowerInformation(SystemBatteryState=5, NULL, 0, ...)` → `SYSTEM_BATTERY_STATE` | 전체 시스템 배터리. `DWORD Rate`를 signed LONG으로 해석. `NTSTATUS=0` 성공. 구조체 32바이트, Rate offset16. AC 어댑터 정격이 아닌 배터리율 |
| WinRT 집계 | `Battery.AggregateBattery.GetReport()` | `BatteryReport.ChargeRateInMilliwatts`는 `int?`, mW, 음수 방전. 미보고/배터리 없음은 null. `Status`와 현재 AC 방향을 함께 검증 |
| WinRT 개별 | `GetDeviceSelector` → `DeviceInformation.FindAllAsync` → `Battery.FromIdAsync` → `GetReport` | 시작 때만 열거, 장치별 보고서 별도 기록. 개별값을 임의로 집계하지 않음 |

`Rate=0`, unknown/null, 반대 방향, 999.9W 초과는 진단의 해석 W가 null이다. Raw는 보존한다. 일부 API에 유효한 숫자가 나와도 실제 전환 후 갱신이 보장되지는 않는다. 특히 전환 첫 샘플에 이전 절댓값의 부호만 바뀌고 다음 샘플부터 unknown이 되면 첫 값과 **unknown 이후 회복시간**을 구분해야 한다. `FirstValidSeconds`는 첫 숫자이며 안정적 회복을 뜻하지 않는다.

집계 API는 여러 배터리용 계약이지만 이 PC의 실측은 단일 절대 단위 배터리다. `SYSTEM_BATTERY_STATE.MaxCapacity`는 문서상 설계 용량 설명이 있으므로 IOCTL FullChargedCapacity 대신 충전 ETA에 섞어 쓰지 않는다. WinRT도 자체 용량/rate 묶음을 기록할 뿐 제품 FULL 계산을 교체하지 않는다. 상대 단위/다중 장치 등은 제품에 새 대체 경로를 넣기 전에 추가 검증해야 한다.

`GetSystemPowerStatus`와 `Windows.System.Power.PowerManager`는 상태/%/시간을 제공하지만 mW rate가 없어 전력 대체 후보가 아니다. WMI는 이번 실측의 우선 대체 경로로 추가하지 않았다. 어떤 API도 수초 회복을 보장하지 않으며 모든 경로가 unknown이면 재조회 빈도 증가로 해결된다고 주장할 수 없다.

제품은 이제 `Native > Estimated > Unavailable` 순서다. Estimated는 같은 단일 절대 단위 장치의 최근 최대60초 endpoint 용량 변화 평균(방향별 부호 있는 delta: 충전 `(end-start)`, 방전 `(start-end)` mWh ×3.6/Δseconds, 양수만 유효)이며 최소15초·두 번 이상 변화가 필요하다. 같은 W 칸을 사용하고 HUD 접두어는 바꾸지 않는다. `IOCTL.Watts`는 제품에 전달하는 값, `NativeWatts`는 원시 경로 값, `PowerSource`와 `Estimate`는 출처/구간/용량/초/실패·초기화 이유다. Native가 있을 때도 shadow estimate를 기록하여 `AbsoluteError`/`PercentageError`로 비교하되 표시값과 섞지 않는다.

30초 동안 의미 있는 용량 변화가 없으면 추정값을 만료한다. 상태/장치/태그/완충 용량 변경, invalid/relative/multiple battery, 모순된 변화, >10초 수집 공백 또는 역행/중복 시각에 이력을 초기화한다. 제품 provider는 Windows Suspend/Resume 이벤트도 요청 플래그로 처리한다. 콘솔 도구는 >10초 공백 감지만 사용하므로 절전 실측 용도가 아니다. Native rate가 양수여도 실제 Capacity가 감소하는 기기는 Estimated CHG를 내보내지 않을 수 있다.

LEFT 우선순위는 유효 Windows lifetime → NativeDerived → EstimatedDerived → Unavailable. 파생 시간은 같은 장치의 절대 RemainingCapacity(mWh)/1000/power(W)*3600초, 최대99h59m이며 현재 방전 방향과 유효 전력이 필요하다. Estimated 만료/reset 시 파생 LEFT도 즉시 unavailable. FULL 우선순위는 NativeDerived → EstimatedDerived → Unavailable(Windows 직접 FULL 시간은 없음). EstimatedDerived FULL은 같은 장치 `(FullChargedCapacity-RemainingCapacity)/1000/Estimated CHG W*3600`초이며 장치 PowerState가 충전 단독(`&6 == 4`)일 때만 계산한다. `IOCTL.FullSource`, `NativeTimeToFullSeconds`, 진단 전용 `ShadowEstimatedFullSeconds`로 출처를 구분한다. `IOCTL.LeftSource`와 `WindowsLifeSeconds`로 출처/실제 OS 미보고를 구분한다. 콘솔 remaining seconds는 정규화된 제품 값이며 실제 HUD 화면 검증과 다르다.

과거 로그를 실제 estimator로 재생하려면 아래 명령을 사용한다. 출력 `Replay=true`이며 새 물리 전환 검증과 구분한다.

```powershell
.\tools\BatteryPowerProbe\bin\Release\net8.0-windows10.0.19041.0\BatteryPowerProbe.exe --replay .\tools\BatteryPowerProbe\bin\Release\net8.0-windows10.0.19041.0\battery-power-comparison.log
```

`summarize.ps1`은 최초의 Native-only 비교 로그용이다. 추정값이 포함된 새 로그에서는 NativeWatts/PowerSource를 구분하여 분석한다.

공식 근거: [SYSTEM_BATTERY_STATE](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-system_battery_state), [CallNtPowerInformation](https://learn.microsoft.com/en-us/windows/win32/api/powerbase/nf-powerbase-callntpowerinformation), [WinRT rate](https://learn.microsoft.com/en-us/uwp/api/windows.devices.power.batteryreport.chargerateinmilliwatts), [BatteryReport null 규칙](https://learn.microsoft.com/en-us/uwp/api/windows.devices.power.batteryreport), [배터리 집계/개별 예제](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/get-battery-info).

현재 사용한 WinRT projection은 기존 앱과 같은 `net8.0-windows10.0.19041.0`, `Microsoft.Windows.SDK.NET.Ref 10.0.19041.56`이다. 설치된 XML/메타데이터로 `AggregateBattery`, nullable rate, mWh 속성을 확인했다.

## 부호 있는 전력 (2026-10-10)

HUD 전력 칸은 `CHG`/`USE` 라벨 없이 부호 있는 W(`+18.4W` 유입, `-19.9W` 유출, `N/A`, 완충·유휴 AC는 `AC`)다. 제품의 `Watts`/`Estimate.Watts`/`NativeWatts`는 모두 부호 있는 값이며 `Flow`는 논리 상태(Charging/Discharging/Ac)로 별도 유지된다. 충전/AC 연결 상태에서 용량이 줄면 음수 Estimated가 유효하고 FULL은 없다. LEFT는 논리 방전 상태에서만, FULL은 양수 전력에서만 계산된다. 이 README 위쪽 설명 중 `abs(ΔmWh)`·CHG/USE 표기는 이전 정책이다. 과거 로그(`--replay`)의 NativeWatts는 크기값이므로 replay는 크기로 비교한다. 비교 대상 `IOCTL.Watts` 외 `SystemBatteryState`/`WinRT`의 `Watts`는 여전히 크기값이다.
