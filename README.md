# AiAgent.Diagnostics — Asynchronous Runtime Telemetry & Agentic Debugging Library

본 저장소는 **.NET 8.0** 애플리케이션의 실행 흐름, 상태 스냅샷, 대용량 페이로드 덤프를 호출 스레드(WPF STA UI 스레드 등) 차단 없이 수집·직렬화하는 고성능 비동기 진단 텔레메트리 라이브러리입니다.

`System.Threading.Channels` 단일 소비자 큐(`AiDiagnosticChannels`)와 `10KB` 임계치 기반 대용량 파일 분리(`ai_dumps/`)를 통해, AI 코딩 에이전트 및 개발자가 컨텍스트 윈도우 오염 없이 런타임 상태를 정밀하게 역추적할 수 있는 구조화된 JSON Lines(`ai_debug.jsonl`) 로그를 생성합니다.

---

## 아키텍처 하이라이트 (Architecture Highlights)

* **Non-Blocking Channel Pipeline**: `Channel.CreateUnbounded<AiDiagnosticEvent>(SingleReader = true)` 기반 백그라운드 전용 워커가 객체 직렬화 및 배치 파일 스트림(`FileShare.ReadWrite`) 기록을 전담하여 메인/UI 스레드 프리징 원천 차단
* **Context-Aware Blob Offloading & Preview**: 직렬화된 페이로드가 **10KB(`10,240 bytes`)**를 초과할 경우 `ai_debug.jsonl` 인라인 기록 대신 `ai_dumps/` 하위 개별 파일(`.json`, Pretty-Printed `.xml`)로 자동 분리 저장하고, 메인 로그에는 `blobPath`와 `160자 요약 미리보기(Preview)`만 기록하여 AI 컨텍스트 윈도우 효율성 보장
* **Cycle-Safe Serialization**: `ReferenceHandler.IgnoreCycles`가 기본 적용되어 부모-자식 양방향 참조가 존재하는 WPF ViewModel, 컨트롤, AST/그래프 모델도 예외 없이 안전하게 직렬화
* **Automatic Session Rotation & Graceful Drain**: 앱 시작(`Start()`) 시 이전 실행의 `ai_debug.jsonl`을 `ai_debug.prev.jsonl`로 자동 백업 및 초기화하여 세션 간 로그 혼선을 방지하고, 종료(`StopAsync()`) 시 취소 토큰 조기 발동 없이 큐에 남은 크래시/종료 이벤트를 끝까지 디스크에 드레인
* **Zero-Overhead Release Guard**: `AiDebugLogger`의 정적 계측 API(`Log`, `Dump`, `BeginScope`, `SaveSnapshot`)는 `#if DEBUG` 조건부 컴파일 및 싱글톤 `NullDisposable` 패턴을 통해 Release 빌드 시 런타임 할당 및 I/O 오버헤드를 제거

---

## 핵심 컴포넌트 (Core Components)

| 클래스 | 파일 | 핵심 책임 |
| :--- | :--- | :--- |
| **`AiDebugLogger`** | [`AiDebugLogger.cs`](AiDebugLogger.cs) | 1줄 정적 계측 퍼사드(`Log`, `Dump`, `BeginScope`, `SaveSnapshot`) 및 호출자 소스 위치(`[CallerFilePath]`, `[CallerLineNumber]`, `[CallerMemberName]`) 자동 캡처 |
| **`AiDiagnosticChannels`** | [`AiDiagnosticChannels.cs`](AiDiagnosticChannels.cs) | 단일 소비자 비동기 채널 큐잉, 세션 로그 회전(`ai_debug.prev.jsonl`), 순환 참조 안전 직렬화, 10KB 초과 페이로드 분리(`ai_dumps/`) 및 Graceful Drain |
| **`AiDiagnosticObserver`** | [`AiDiagnosticObserver.cs`](AiDiagnosticObserver.cs) | 전역 `DiagnosticListener.AllListeners` 구독(`SubscribedPrefixes` 설정 가능) 및 익명/구조화 페이로드 리플렉션 추출기 |
| **`AiDiagnosticEvent`** | [`AiDiagnosticEvent.cs`](AiDiagnosticEvent.cs) | 타임스탬프, 로그 레벨, 스코프, 소요 시간(`DurationMs`), 호출자 메타데이터, 원본 페이로드를 담는 내부 이벤트 DTO |

---

## 수명주기 통합 및 사용법 (Lifecycle & API Usage)

### 1. 애플리케이션 수명주기 연동 (`Startup` / `Exit`)

애플리케이션 시작 시 백그라운드 채널 워커를 기동(이전 세션 로그는 `ai_debug.prev.jsonl`로 자동 회전)하고, 종료 시 큐에 남은 잔여 이벤트를 안전하게 플러시합니다.

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

### 2. 표준 계측 사용법 (`AiDebugLogger`)

AI 에이전트 또는 개발자가 런타임 상태를 추적할 때는 단일 정적 퍼사드인 `AiDebugLogger`를 사용합니다. 임시 디버깅용 탐침 코드를 삽입할 때는 커밋 전 일괄 검색·제거가 쉽도록 `// [AI-DEBUG]` 주석을 함께 표기하는 것을 권장합니다.

```csharp
using AiAgent.Diagnostics;

// 1. 일반 실행 흐름 로깅
AiDebugLogger.Log("분석 파이프라인 초기화 완료", level: "INFO", scope: "Pipeline"); // [AI-DEBUG]

// 2. 구간 소요 시간(ms) 자동 측정 (Dispose 시 END 이벤트 및 DurationMs 기록)
using (AiDebugLogger.BeginScope("CtuAstGeneration")) // [AI-DEBUG]
{
    // 3. 임의 객체 또는 XML/JSON 문자열 덤프 (10KB 초과 시 ai_dumps/ 파일로 자동 분리)
    AiDebugLogger.Dump("CompileArguments", argsList, scope: "CTU"); // [AI-DEBUG]
}

// 4. 특정 시점의 상태 스냅샷 기록
AiDebugLogger.SaveSnapshot("PresetSyncState", currentPresetModel); // [AI-DEBUG]
```

### 3. 계측 API 요약

| 메서드 | 주요 용도 | 페이로드 저장 방식 |
| :--- | :--- | :--- |
| `AiDebugLogger.Log(message, level, scope)` | 단일 실행 흐름 및 상태 메시지 기록 | `ai_debug.jsonl` 인라인 메시지 기록 |
| `AiDebugLogger.Dump(label, obj, scope)` | 복합 객체(순환 참조 포함), 예외(`Exception`), XML/JSON 원문 덤프 | 10KB 이하: 인라인 기록<br>10KB 초과: `ai_dumps/dump_<timestamp>_<label>.(json\|xml)` 분리 저장 + 160자 Preview 기록 |
| `AiDebugLogger.BeginScope(scopeName)` | `using` 블록 구간의 시작(`START`)·종료(`END`) 및 경과 시간(`durationMs`) 측정 | `ai_debug.jsonl` 인라인 기록 |
| `AiDebugLogger.SaveSnapshot(snapshotId, state)` | 루프 회차별 또는 상태 전이 전후의 객체 스냅샷 보존 | `Dump`와 동일 (10KB 임계치 기반 자동 분리) |

---

## 출력 로그 스키마 (Output Schema)

* **현재 세션 로그 파일**: `<BasePath>/ai_debug.jsonl`
* **직전 세션 백업 파일**: `<BasePath>/ai_debug.prev.jsonl`
* **대용량 덤프 디렉터리**: `<BasePath>/ai_dumps/`

```json
{
  "timestamp": "2026-11-03T17:35:00.125+09:00",
  "level": "DUMP",
  "message": "[Offloaded] OutputXml (48210 chars). Saved to file: C:\\...\\ai_dumps\\dump_20261103_173500_125_OutputXml.xml | Preview: <root>   <table id=\"1\">...",
  "scope": "XML_PROCESS",
  "durationMs": null,
  "caller": {
    "file": "HwpFalseAlarmReportGenerator.cs",
    "line": 142,
    "method": "GenerateAsync"
  },
  "blobPath": "C:\\...\\ai_dumps\\dump_20261103_173500_125_OutputXml.xml",
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
