# DevOverlay

Windows용 시스템·개발 지표 오버레이입니다. 한 줄짜리 HUD에 CPU, GPU, 저장장치, 네트워크, FPS, 프레임 타임, 지연 시간, Codex/Claude 사용량을 그룹별로 표시합니다.

현재 버전: **v0.1.2** (Windows 10/11 x64)

## 구현된 기능

- 투명한 항상 위 오버레이 창과 트레이 아이콘(Open Settings / Show/Hide Overlay / Exit)
- CPU 사용률, CPU 패키지 온도·전력(Sensor Service + PawnIO 필요)
- NVIDIA GPU 사용률·온도·전력·VRAM(NVIDIA 드라이버의 NVML 사용)
- 디스크 읽기/쓰기, 네트워크 업로드/다운로드 및 오늘 사용량(장치 선택 가능)
- FPS, 1% Low(표시 간격 기준, 최근 3초 창의 가장 느린 1% 평균), 프레임 타임, Render Present Latency(Intel PresentMon 서비스 필요)
- Codex 계정 한도, Claude 사용량(각 CLI가 설치되어 있어야 함)
- Settings: 그룹 표시/순서, 위치(이동/초기화), 갱신 주기(250–2000 ms), 색상·투명도 등 외형, HUD 전역 단축키(기본 `Ctrl+Shift+O`)

## 설치와 첫 실행

1. `DevOverlay-v0.1.2-win-x64.zip`을 원하는 폴더에 압축 해제합니다. 앱 자체는 휴대용이며 .NET 설치가 필요 없습니다(self-contained, 단일 파일 `DevOverlay.exe`).
   첫 실행 시 필요한 네이티브 구성 요소를 준비하므로 평소보다 몇 초 더 걸릴 수 있습니다. 이 구성 요소는 `%TEMP%\.net\DevOverlay\`에 풀리며 앱 폴더에는 아무것도 쓰지 않습니다. 이후 실행은 다시 풀지 않아 평소와 같은 속도로 시작됩니다.
2. `DevOverlay.exe`를 실행합니다. 서명되지 않은 바이너리이므로 Windows SmartScreen 경고가 나올 수 있습니다.
3. 트레이 아이콘의 **Open Settings**로 설정을 엽니다. 설정은 `%LOCALAPPDATA%\DevOverlay\settings.json`에 저장됩니다.

### 선택 구성 요소(자동 설치되지 않음)

| 기능 | 필요한 것 | 방법 |
| --- | --- | --- |
| FPS / 1% Low / Frame Time / Latency | Intel PresentMon 서비스 **v2.6.0** | Settings의 *Get PresentMon installer*가 공식 릴리스 페이지를 엽니다. 직접 설치해야 합니다. 설치되어 있지 않으면 해당 값은 N/A로 표시됩니다. |
| CPU 패키지 온도·전력 | DevOverlay Sensor Service + PawnIO | Settings > CPU의 *Install Sensor Service*(UAC 승인 필요). PawnIO는 같은 화면에서 명시적으로 요청할 때만 다운로드하며(SHA-256 검증 후) UAC 승인으로 설치합니다. 시작 시 자동 설치하지 않습니다. |
| Codex / Claude 사용량 | `codex` / `claude` CLI 설치와 로그인 | Settings의 AI Usage 안내를 따르세요. Claude 상태줄 연동은 `%USERPROFILE%\.claude\settings.json`을 수정하며, 기존 사용자 정의 statusLine은 덮어쓰지 않습니다. |

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
