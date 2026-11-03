# AiAgent.Diagnostics — Non-Intrusive Asynchronous Telemetry & Diagnostics Library

본 저장소는 **.NET 8.0** 애플리케이션의 실행 흐름, 상태 스냅샷, 대용량 페이로드 덤프를 호출 스레드(WPF STA UI 스레드 등) 차단 없이 수집·직렬화하는 고성능 비동기 진단 텔레메트리 라이브러리입니다.

도메인 계층의 결합도를 낮추는 `.NET DiagnosticSource` 기반 비침투적 이벤트 구독(`AiDiagnosticObserver`)과 `System.Threading.Channels` 단일 소비자 큐(`AiDiagnosticChannels`)를 결합하여, AI 에이전트 및 개발자가 런타임 상태를 정밀하게 역추적할 수 있는 구조화된 JSON Lines(`ai_debug.jsonl`) 로그를 생성합니다.

---

## 아키텍처 하이라이트 (Architecture Highlights)

* **Non-Blocking Channel Pipeline**: `Channel.CreateUnbounded<AiDiagnosticEvent>(SingleReader = true)` 기반 백그라운드 전용 워커가 객체 직렬화 및 디스크 I/O를 전담하여 메인/UI 스레드 프리징 원천 차단
* **Decoupled Observability**: 비즈니스 로직에서 로거를 직접 참조하지 않고 표준 `System.Diagnostics.DiagnosticListener`로 발행한 이벤트를 `AiDiagnosticObserver`가 자동 수집·변환
* **Context-Aware Blob Offloading**: 직렬화된 페이로드가 **10KB(`10,240 bytes`)**를 초과할 경우 `ai_debug.jsonl` 인라인 기록 대신 `ai_dumps/` 하위 개별 파일(`.json`, Pretty-Printed `.xml`)로 자동 분리 저장하고 `blobPath` 참조만 기록하여 로그 가독성 및 AI 컨텍스트 윈도우 효율성 보장
* **Zero-Overhead Release Guard**: `AiDebugLogger`의 정적 계측 API(`Log`, `Dump`, `BeginScope`, `SaveSnapshot`)는 `#if DEBUG` 조건부 컴파일 및 싱글톤 `NullDisposable` 패턴을 통해 Release 빌드 시 런타임 할당 및 I/O 오버헤드를 제거
* **Strict Telemetry Isolation**: 호스트 애플리케이션(예: [`ArqaStatic`](../README.md))에서 외부 정적 분석 엔진의 원시 입출력 스트림(`AnalysisTraceLogger`)과 애플리케이션 수명주기/AI 진단 텔레메트리를 물리적으로 분리

---

## 핵심 컴포넌트 (Core Components)

| 클래스 | 파일 | 핵심 책임 |
| :--- | :--- | :--- |
| **`AiDebugLogger`** | [`AiDebugLogger.cs`](AiDebugLogger.cs) | 정적 계측 퍼사드(`Log`, `Dump`, `BeginScope`, `SaveSnapshot`) 및 호출자 소스 위치(`[CallerFilePath]`, `[CallerLineNumber]`, `[CallerMemberName]`) 자동 캡처 |
| **`AiDiagnosticChannels`** | [`AiDiagnosticChannels.cs`](AiDiagnosticChannels.cs) | 무제한 단일 소비자 비동기 채널 큐잉, 10KB 임계치 기반 대용량 페이로드 분리(`ai_dumps/`), `ai_debug.jsonl` 비동기 파일 기록 및 Graceful Shutdown 플러시 |
| **`AiDiagnosticObserver`** | [`AiDiagnosticObserver.cs`](AiDiagnosticObserver.cs) | 전역 `DiagnosticListener.AllListeners` 구독 및 익명/구조화 페이로드 리플렉션 추출기(`InnerEventObserver`) |
| **`AiDiagnosticEvent`** | [`AiDiagnosticEvent.cs`](AiDiagnosticEvent.cs) | 타임스탬프, 로그 레벨, 스코프, 소요 시간(`DurationMs`), 호출자 메타데이터, 원본 페이로드를 담는 내부 이벤트 DTO |

---

## 수명주기 통합 및 사용법 (Lifecycle & API Usage)

### 1. 애플리케이션 수명주기 연동 (`Startup` / `Exit`)

애플리케이션 시작 시 백그라운드 채널 워커와 옵저버를 기동하고, 종료 시 큐에 남은 잔여 이벤트를 안전하게 플러시합니다.

```csharp
// App.xaml.cs (WPF) 또는 Program.cs
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

#if DEBUG
    AiAgent.Diagnostics.AiDebugLogger.BasePath = AppDomain.CurrentDomain.BaseDirectory;
    AiAgent.Diagnostics.AiDiagnosticChannels.Start();
    AiAgent.Diagnostics.AiDiagnosticObserver.Register();
#endif
}

protected override void OnExit(ExitEventArgs e)
{
#if DEBUG
    try
    {
        AiAgent.Diagnostics.AiDiagnosticObserver.Unregister();
        AiAgent.Diagnostics.AiDiagnosticChannels.StopAsync().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[AiAgent.Diagnostics] Shutdown error: {ex.Message}");
    }
#endif
    base.OnExit(e);
}
```

### 2. 계측 방식 A — 정적 퍼사드 호출 (`AiDebugLogger`)

빠른 디버깅, 성능 구간 측정 및 상태 스냅샷 기록 시 사용합니다.

```csharp
using AiAgent.Diagnostics;

// 1. 일반 실행 흐름 로깅
AiDebugLogger.Log("분석 파이프라인 초기화 완료", level: "INFO", scope: "Pipeline");

// 2. 구간 소요 시간(ms) 자동 측정 (Dispose 시 END 이벤트 및 DurationMs 기록)
using (AiDebugLogger.BeginScope("CtuAstGeneration"))
{
    // 3. 임의 객체 또는 XML/JSON 문자열 덤프 (10KB 초과 시 ai_dumps/ 파일로 자동 분리)
    AiDebugLogger.Dump("CompileArguments", argsList, scope: "CTU");
}

// 4. 특정 시점의 상태 스냅샷 기록
AiDebugLogger.SaveSnapshot("PresetSyncState", currentPresetModel);
```

### 3. 계측 방식 B — 비침투적 이벤트 발행 (`DiagnosticListener`)

도메인 서비스 계층에서 `AiAgent.Diagnostics` 어셈블리 직접 참조 없이 표준 `.NET` API로 이벤트를 발행합니다 (`ArqaStatic*` 또는 `AiAgent.Diagnostics*` 접두사 리스너 자동 구독).

```csharp
using System.Diagnostics;

public class ReportGenerator
{
    private static readonly DiagnosticSource s_diag =
        new DiagnosticListener("ArqaStatic.ReportGenerator");

    public void Generate(string outputPath, object reportModel, string rawXml)
    {
        s_diag.Write("ReportStarted", new
        {
            Message = $"Generating report: {outputPath}",
            Level = "INFO",
            Scope = "Report"
        });

        s_diag.Write("ReportXmlDump", new
        {
            Label = "DocumentXml",
            Xml = rawXml,
            Level = "DUMP",
            Scope = "Report"
        });
    }
}
```

### 4. 계측 API 요약

| 메서드 | 주요 용도 | 페이로드 저장 방식 |
| :--- | :--- | :--- |
| `AiDebugLogger.Log(message, level, scope)` | 단일 실행 흐름 및 상태 메시지 기록 | `ai_debug.jsonl` 인라인 메시지 기록 |
| `AiDebugLogger.Dump(label, obj, scope)` | 복합 객체, 예외(`Exception`), XML/JSON 원문 덤프 | 10KB 이하: 인라인 기록<br>10KB 초과: `ai_dumps/dump_<timestamp>_<label>.(json\|xml)` 분리 저장 |
| `AiDebugLogger.BeginScope(scopeName)` | `using` 블록 구간의 시작(`START`)·종료(`END`) 및 경과 시간(`durationMs`) 측정 | `ai_debug.jsonl` 인라인 기록 |
| `AiDebugLogger.SaveSnapshot(snapshotId, state)` | 루프 회차별 또는 상태 전이 전후의 객체 스냅샷 보존 | `Dump`와 동일 (10KB 임계치 기반 자동 분리) |

---

## 출력 로그 스키마 (Output Schema)

* **메인 로그 파일**: `<BasePath>/ai_debug.jsonl`
* **대용량 덤프 디렉터리**: `<BasePath>/ai_dumps/`

```json
{
  "timestamp": "2026-11-03T15:30:00.125+09:00",
  "level": "DUMP",
  "message": "[Offloaded] OutputXml (48210 chars). Saved to file: C:\\...\\ai_dumps\\dump_20261103_153000_125_OutputXml.xml",
  "scope": "XML_PROCESS",
  "durationMs": null,
  "caller": {
    "file": "HwpFalseAlarmReportGenerator.cs",
    "line": 142,
    "method": "GenerateAsync"
  },
  "blobPath": "C:\\...\\ai_dumps\\dump_20261103_153000_125_OutputXml.xml",
  "data": null
}
```

---

## 빌드 및 서브모듈 연동 (Build & Submodule Integration)

### 요구 사항 (Prerequisites)
* **SDK**: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 이상 (`TargetFramework: net8.0`)

### 단독 빌드 및 서브모듈 추가
```powershell
# 1. 프로젝트 단독 빌드
dotnet build AiAgent.Diagnostics.csproj

# 2. 상위 저장소에 Git 서브모듈로 등록 시
git submodule add https://github.com/jin20203458/AiAgent.Diagnostics.git AiAgent.Diagnostics
git submodule update --init --recursive
```

상위 프로젝트(`.csproj`) 참조 설정:
```xml
<ItemGroup>
  <ProjectReference Include="..\AiAgent.Diagnostics\AiAgent.Diagnostics.csproj" />
</ItemGroup>
```
