# DevOverlay

Windows에서 시스템 및 개발 관련 지표를 작고 구성 가능한 오버레이 형태로 표시하기 위한 WPF 애플리케이션입니다.

> 현재는 초기 기반 구현 단계입니다. CPU 사용률만 실제 Windows API로 수집하며, 나머지 지표는 화면 구조 검증을 위한 데모 값입니다.

## 현재 구현 상태

- 투명한 항상 위 표시(WPF) 오버레이 창
- CPU, GPU, 프레임, 지연 시간별 가로 그룹 UI
- 비동기 `IMetricProvider` 기반 수집 구조와 공급자별 갱신 주기
- Windows `GetSystemTimes` API 기반 전체 CPU 사용률 수집(1초 간격)
- 지표 ID, 카테고리, 단위, 사용 가능 여부, 갱신 시각을 갖는 정규화된 `MetricSnapshot` 모델
- 화면 표시와 지표 수집을 분리한 구조
- 위치, 표시 지표, 팝업 동작을 위한 설정 모델

첫 CPU 샘플은 이전 값과의 차이를 계산할 수 없으므로 `—`로 표시됩니다. 이후 갱신부터 실제 CPU 사용률이 표시됩니다.

## 예정 기능

- 실제 GPU 사용률·온도·전력·VRAM 수집
- 저장장치·네트워크 지표
- FPS, 1% Low FPS, 프레임 타임, 지연 시간 수집
- Codex 및 Claude 사용량 연동
- 설정 저장, 다중 모니터 위치 선택, 드래그 이동
- 클릭·호버 기반 상세 팝업

예정 항목은 아직 구현되지 않았습니다.

## 요구 사항

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 이상

현재 프로젝트는 `net8.0-windows`와 WPF를 사용합니다. 런타임만 설치되어 있으면 빌드는 할 수 없으므로 **SDK**를 설치해야 합니다.

## 실행 방법

PowerShell에서 다음을 실행합니다.

```powershell
git clone https://github.com/jimmy8790/DevOverlay.git
Set-Location DevOverlay
dotnet run
```

SDK 설치 여부는 다음 명령으로 확인할 수 있습니다.

```powershell
dotnet --list-sdks
```

## 프로젝트 구조

```text
Configuration/       오버레이 위치와 표시 옵션 모델
Metrics/             지표 모델, 공급자 인터페이스, 갱신 서비스
Metrics/Windows/     Windows 전용 실제 지표 공급자
Metrics/Demo/        UI 검증용 데모 지표 공급자
Platform/Windows/    Windows 위치 지정 기능
Presentation/        오버레이용 ViewModel
UI/                  WPF 오버레이 화면
```

## 개발 상태

이 저장소의 소스는 초기 구현 단계입니다. 개발 환경에는 .NET 8 런타임은 설치되어 있었지만 SDK가 없어, 현재 버전의 `dotnet build` 및 앱 실행은 아직 검증되지 않았습니다. SDK 설치 후 `dotnet build`와 `dotnet run`으로 확인해야 합니다.
