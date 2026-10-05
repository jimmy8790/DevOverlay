# DevOverlay

Windows용 시스템·개발 지표 오버레이입니다. 한 줄짜리 HUD에 CPU, GPU, 저장장치, 네트워크, FPS, 프레임 타임, 지연 시간, Codex/Claude 사용량, 무선 주변기기 배터리를 그룹별로 표시합니다.

현재 버전: **v0.2.1** (Windows 10/11 x64)

## 구현된 기능

- 투명한 항상 위 오버레이 창과 트레이 아이콘(Open Settings / Show/Hide Overlay / Exit)
- CPU 사용률, CPU 패키지 온도·전력(Sensor Service + PawnIO 필요)
- NVIDIA GPU 사용률·온도·전력·VRAM(NVIDIA 드라이버의 NVML 사용)
- 디스크 읽기/쓰기, 네트워크 업로드/다운로드 및 오늘 사용량(장치 선택 가능)
- FPS, 1% Low(표시 간격 기준, 최근 3초 창의 가장 느린 1% 평균), 프레임 타임, Render Present Latency(Intel PresentMon 서비스 필요)
- Codex 계정 한도, Claude 사용량(각 CLI가 설치되어 있어야 함)
- 무선 주변기기 배터리(마우스, 키보드, 헤드셋 등): Windows·Bluetooth·HID 표준 경로와 검증된 제조사 경로 중 숫자를 제공하는 기기만 표시
- Settings 상단 About: 실행 중인 버전, GitHub 저장소·Releases 링크, 새 안정 버전 알림
- Settings 상단 General: *Start DevOverlay with Windows*(Windows 로그인 시 자동 시작, 선택 사항)
- Settings: 그룹 표시/순서, 위치(이동/초기화), 갱신 주기(250–2000 ms), 색상·투명도 등 외형, HUD 전역 단축키(기본 `Ctrl+Shift+O`)

## 설치와 첫 실행

1. `DevOverlay-v0.2.1-win-x64.zip`을 원하는 폴더에 압축 해제합니다. 앱 자체는 휴대용이며 .NET 설치가 필요 없습니다(self-contained, 단일 파일 `DevOverlay.exe`).
   첫 실행 시 필요한 네이티브 구성 요소를 준비하므로 평소보다 몇 초 더 걸릴 수 있습니다. 이 구성 요소는 `%TEMP%\.net\DevOverlay\`에 풀리며 앱 폴더에는 아무것도 쓰지 않습니다. 이후 실행은 다시 풀지 않아 평소와 같은 속도로 시작됩니다.
2. `DevOverlay.exe`를 실행합니다. 서명되지 않은 바이너리이므로 Windows SmartScreen 경고가 나올 수 있습니다.
3. 트레이 아이콘의 **Open Settings**로 설정을 엽니다. 설정은 `%LOCALAPPDATA%\DevOverlay\settings.json`에 저장됩니다.

### 선택 구성 요소(자동 설치되지 않음)

| 기능 | 필요한 것 | 방법 |
| --- | --- | --- |
| FPS / 1% Low / Frame Time / Latency | Intel PresentMon 서비스 **v2.6.0** | Settings의 *Get PresentMon installer*가 공식 릴리스 페이지를 엽니다. 직접 설치해야 합니다. 설치되어 있지 않으면 해당 값은 N/A로 표시됩니다. |
| CPU 패키지 온도·전력 | DevOverlay Sensor Service + PawnIO | Settings > CPU의 *Install Sensor Service*(UAC 승인 필요). PawnIO는 같은 화면에서 명시적으로 요청할 때만 다운로드하며(SHA-256 검증 후) UAC 승인으로 설치합니다. 시작 시 자동 설치하지 않습니다. |
| Codex / Claude 사용량 | `codex` / `claude` CLI 설치와 로그인 | Settings의 AI Usage 안내를 따르세요. Claude 상태줄 연동은 `%USERPROFILE%\.claude\settings.json`을 수정하며, 기존 사용자 정의 statusLine은 덮어쓰지 않습니다. |

## Windows 시작 시 자동 실행

Settings > General의 *Start DevOverlay with Windows*를 켜면 Windows에 로그인할 때 DevOverlay가 자동으로 시작됩니다. 기본값은 꺼짐입니다.

- 현재 사용자 계정의 시작 항목(`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`의 `DevOverlay` 값)만 사용합니다. 관리자 권한과 UAC가 필요 없고, Windows 서비스나 시스템 전체 시작 항목을 만들지 않으며, 다른 프로그램의 시작 항목은 건드리지 않습니다. 끄면 `DevOverlay` 값만 삭제됩니다.
- 체크박스는 저장된 설정이 아니라 실제 레지스트리 상태를 보여 줍니다. 항목을 직접 지웠다면 체크가 풀린 채로 표시됩니다.
- 휴대용 앱이라 폴더를 옮기면 경로가 바뀝니다. 이미 켜 둔 상태에서 새 위치의 `DevOverlay.exe`를 직접 실행하면 시작 항목이 새 경로로 자동 갱신됩니다. 꺼 둔 상태를 자동으로 켜지는 않습니다.
- 자동 시작해도 평소 실행과 같습니다(트레이, HUD, 전역 단축키 등). Sensor Service 설치나 관리자 승인을 요청하지 않습니다.
- 이미 실행 중인 DevOverlay를 다시 실행하면 두 번째 **GUI** 인스턴스는 조용히 종료됩니다. Windows 시작 항목과 수동 실행이 겹쳐도 HUD·트레이·전역 단축키가 중복 생성되지 않습니다. Claude 상태줄 연동(`--claude-status-line`)은 별도 짧은 실행 경로라 이 제한을 받지 않습니다.

## 주변기기 배터리

Settings의 *Peripheral batteries*에서 HUD에 표시할 기기를 고릅니다. 기기가 숫자 배터리를 노출하는 경우에만 퍼센트를 표시하며, 값을 읽을 수 없으면 추측하지 않고 `N/A`로 표시합니다.

- **읽는 방법**: Windows 장치 속성, Bluetooth 표준 Battery Service, 표준 HID Battery Strength, 그리고 아래에 적은 검증된 제조사 경로입니다. 모든 독점 2.4 GHz 수신기가 배터리 퍼센트를 노출하는 것은 아니므로 지원되지 않는 기기는 `N/A`로 남을 수 있습니다. DevOverlay는 배터리 값을 만들어내지 않습니다.
- **검증된 제조사 경로**
  - Pulsar: USB 수신기 `3710:5502`(Pulsar LED 8K Dongle)에서 실기기로 숫자 배터리를 확인했습니다. 공식 Pulsar 앱과 같이 전압 정보가 있으면 전압 곡선으로, 없으면 기기가 보고한 레벨로 계산하므로, 앱의 화면 보정 때문에 1 정도 다를 수 있습니다. `3710:5406`은 공개 자료의 같은 프로토콜로 인식하지만 실기기로는 검증하지 않았습니다. 다른 Pulsar 모델 전반의 지원은 보장하지 않습니다.
  - Razer Barracuda X 2.4(`1532:0550`): 연결 상태만 확인합니다. 테스트한 PC 2.4 GHz 수신기는 배터리 요청에 응답하지 않아 숫자 배터리는 지원하지 않으며 `N/A`로 표시됩니다.
- **HUD 표시**: `BAT` 제목 없이 기기 종류별 짧은 이름을 씁니다: `MSE`(마우스), `KB`(키보드), `HS`(헤드셋), `PAD`(컨트롤러), `BUD`(이어버드), `DEV`(기타). 같은 종류가 여러 개면 `MSE2`처럼 번호가 붙습니다. 예: `MSE 98% KB 64%`.
- **HUD 이름 바꾸기**: 기기마다 최대 12자의 이름을 지정할 수 있습니다(예: `X2 98%`). 비워 두면 기본 이름으로 돌아갑니다. 값이 읽히는 기기만 처음부터 표시되며, 지원되지 않는 기기는 숨김 상태로 시작합니다.
- **Settings 목록**: 기본적으로 현재 연결된 기기만 보여 줍니다. 연결되지 않은 저장 기기는 목록에서 숨겨질 뿐 이름·표시 설정은 그대로 보관되며, *Show disconnected saved devices*를 켜면 다시 나타납니다(이 체크는 저장되지 않고 Settings를 열 때마다 꺼진 상태로 시작합니다).
- **Forget**: *Show disconnected saved devices*를 켜면 연결되어 있지 않은 저장 기기에 *Forget* 버튼이 나타납니다. DevOverlay에 저장된 해당 기기의 이름·표시 설정만 지우며, Windows 장치·드라이버·Bluetooth 페어링·수신기와 기기 자체는 변경하지 않습니다. 같은 기기가 다시 감지되면 기본 설정의 새 항목으로 나타납니다.
- **갱신 주기**: 주변기기는 CPU/GPU/FPS 같은 고빈도 지표와 별개로 약 45초마다 읽으며, 기기 연결·분리 알림이 있으면 더 빨리 갱신됩니다.

### Export Peripheral Diagnostics

지원되지 않는 기기를 진단하거나 앞으로의 호환성을 넓히는 데 쓰는 진단 파일입니다. Settings의 *Export Peripheral Diagnostics*를 누르고 저장 위치를 고를 때만 JSON 파일이 만들어지며, 자동으로 전송되지 않습니다.

- 포함: 제품 이름, 해시 처리한 기기 식별자, 기기 종류, 배터리 읽기 방식별 결과, HID 인터페이스의 VID/PID·UsagePage/UsageId·보고서 길이·ID, 제조사 경로 일치 여부.
- 제외: 원본 일련번호, Bluetooth 주소, 원본 장치 인스턴스 경로·컨테이너 ID, 사용자 지정 HUD 이름, HID 보고서 내용(raw payload), 예외 메시지.
- 제품 이름은 사용자 정보일 수 있고 해시도 같은 기기를 구분하는 식별자이므로 완전한 익명성을 보장하지 않습니다. 공유하기 전에 파일을 열어 확인하세요.

## 업데이트 확인

Settings 맨 위의 *About*에 실행 중인 버전이 표시되고, Settings를 열 때 GitHub Releases에서 새 **안정** 버전이 있는지 확인합니다. 시작 시에는 확인하지 않으며 결과는 몇 시간 동안 보관됩니다(실패한 경우는 짧게).

- 새 버전이 있으면 안내와 함께 *View release*로 해당 GitHub Release 페이지를 엽니다. *Check again*은 보관된 결과를 무시하고 다시 확인하며, *Open GitHub* / *View releases*는 저장소와 릴리스 목록을 기본 브라우저로 엽니다.
- 공개 GitHub API만 사용하고 로그인이 필요 없습니다. 요청에는 앱 이름·버전(User-Agent)만 포함되며 하드웨어·설정·기기 정보는 보내지 않습니다.
- 인터넷이 없거나 GitHub 응답이 실패해도 DevOverlay의 일반 동작에는 영향이 없고 *Could not check for updates.*만 표시됩니다.
- draft와 prerelease는 무시합니다.
- **알림 기능일 뿐입니다.** DevOverlay는 새 버전을 자동으로 다운로드하거나 설치하지 않으며 자동 업데이트 프로그램이 아닙니다. 새 ZIP은 직접 받아 압축을 풀어야 합니다.

### Sensor Service 설치 위치

Sensor Service는 **LocalSystem 권한의 Windows 서비스**입니다. 압축을 푼 폴더의 `SensorService\`(`DevOverlay.SensorService.exe` 단일 파일과 네이티브 헬퍼 `MonoPosixHelper.dll`, `libMonoPosixHelper.dll`)는 설치 원본으로만 쓰이며, 서비스가 직접 실행하지 않습니다. 서비스는 네이티브 코드를 임시 폴더로 풀지 않고, 보호된 설치 폴더의 파일만 로드합니다.

- *Install / Repair Sensor Service*는 관리자 권한으로 원본을 `%ProgramFiles%\DevOverlaySensorService\current\`에 복사합니다.
- 이 폴더는 SYSTEM과 Administrators만 수정할 수 있고 Users는 읽기·실행만 가능하도록 ACL이 설정됩니다. 설치 프로그램은 이 ACL과 소유자를 검증한 뒤에만 서비스를 해당 위치로 등록합니다.
- 앱 폴더를 옮기거나 삭제해도 서비스 경로는 바뀌지 않습니다. 새 버전으로 갱신하려면 새 폴더에서 *Repair Sensor Service*를 실행하세요.
- 예전 개발 빌드처럼 보호되지 않은 경로에 등록된 서비스는 Settings에 *Broken — unprotected location*으로 표시됩니다. *Repair*로 보호 위치로 옮길 수 있습니다. Repair가 실패하면 서비스는 중지되고 비활성화됩니다.
- *Uninstall Sensor Service*는 서비스와 `%ProgramFiles%\DevOverlaySensorService\`만 제거합니다(PawnIO는 제거하지 않음).
- 설치·복구 실패 내용은 Windows Application 이벤트 로그(원본 `DevOverlaySensorService`)에 기록됩니다.

Claude 상태줄 연동은 `DevOverlay.exe`의 경로를 기록하므로, 앱 폴더를 옮기면 다시 설정해야 합니다.

## 알려진 제한

- 1% Low는 최근 약 3초의 프레임을 기준으로 계산되어 현재 게임 상태를 빠르게 반영합니다. 최근 3초 동안 화면에 표시된 프레임 중 가장 느린 1%의 평균으로 계산합니다. 긴 프레임(히치)이 생기면 약 3초 동안 값이 내려갔다가 돌아옵니다. 30 FPS 근처에서는 3초 창의 프레임 수가 적어 가장 느린 프레임 1개가 값이 되므로, 높은 FPS보다 값이 더 크게 움직입니다. 다른 오버레이와는 창 길이·표시 지연·집계 방식이 달라 순간값이 다를 수 있습니다.
- Frame Generation처럼 하나의 Present가 여러 번 표시되는 경우, 같은 Present QPC가 반복된 행은 1% Low 표본에서 제외되어 표시 이벤트가 적게 계산될 수 있습니다.
- Windows x64만 지원합니다. 이 릴리스는 코드 서명되어 있지 않습니다.

## 소스에서 빌드

.NET 8 SDK가 필요합니다.

```powershell
git clone https://github.com/jimmy8790/DevOverlay.git
Set-Location DevOverlay
dotnet build DevOverlay.csproj -c Release
dotnet test DevOverlay.Tests -c Release
.\tools\package-release.ps1   # release\DevOverlay-v<version>-win-x64.zip 생성
```

## 라이선스

DevOverlay 자체 소스는 [MIT License](LICENSE)입니다(Copyright (c) 2026 DevOverlay).

배포 ZIP에 포함된 서드파티 구성 요소는 각자의 라이선스를 따르며 DevOverlay의 MIT 라이선스로 재라이선스되지 않습니다. [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)와 ZIP의 `licenses\` 폴더를 참고하세요.

앱 아이콘은 프로젝트 로고(`assets/DevOverlay-logo.png`)에서 `tools/generate-icons.ps1`로 만든 `assets/DevOverlay.ico`입니다.
