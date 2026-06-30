# AiAgent.Diagnostics

AI 에이전트가 소프트웨어를 자율적으로 디버깅하고 코드를 분석할 수 있도록 최적화된 **C#/.NET 고성능 스마트 진단 라이브러리**입니다.

이 라이브러리는 비즈니스 도메인 코드를 오염시키지 않는 **비침투적 이벤트 설계(DiagnosticSource)**와 호출 스레드를 방해하지 않는 **논블로킹 비동기 큐(Channels)**를 결합하여 구현되었습니다.

---

## 🌟 핵심 기능

1. **비침투적 설계 (Decoupled Logging)**
   * 비즈니스 로직 코드 내부에서 로깅 라이브러리를 직접 참조하여 강하게 결합하지 않습니다.
   * .NET 표준 `System.Diagnostics.DiagnosticSource`를 사용해 이벤트를 발행(Publish)하면, 전역 리스너(`AiDiagnosticObserver`)가 이를 구독하여 안전하게 적재합니다.

2. **비동기 논블로킹 채널 (Async Processing)**
   * `System.Threading.Channels` 기반의 단일 소비자(Single Reader) 큐를 사용합니다.
   * 무거운 객체 직렬화(Serialization) 및 디스크 I/O 작업이 백그라운드 스레드에서 비동기 처리되므로, WPF UI 등의 메인 스레드가 절대 정지(Freezing)되지 않습니다.

3. **스마트 데이터 분리 (Blob Offloading)**
   * AI 에이전트의 컨텍스트 윈도우(Context Window) 한계를 초과하는 대용량 데이터(예: 수만 라인의 XML, 큰 JSON 데이터 구조 등)를 식별합니다.
   * **10KB를 초과하는 데이터**는 로그 라인에 집어넣지 않고, `ai_dumps/` 디렉터리에 독립된 파일(`.xml`, `.json`)로 자동 분리 저장하고 참조 경로만 남겨 로그를 깨끗하게 유지합니다.
   * XML 데이터는 읽기 좋게 들여쓰기 정렬(Pretty Print)을 자동 수행하여 AI 에이전트의 가독성을 극대화합니다.

4. **구조화된 로그 포맷 (JSON Lines)**
   * 로그는 줄바꿈 단위의 JSON 포맷(`ai_debug.jsonl`)으로 누적 저장됩니다.
   * 타임스탬프, 레벨, 스코프, 소요 시간, 호출 부모 정보(파일명, 행 번호, 호출 메서드명)가 상세히 파싱되어 AI 에이전트가 즉각 소스 코드 내 오류 발생 위치로 이동할 수 있습니다.

---

## 🛠️ 설치 및 초기화

### 1. 파이프라인 시작 및 옵저버 등록 (앱 Startup)
애플리케이션 시작 시점에 백그라운드 채널 워커를 시작하고, 이벤트를 모니터링할 옵저버를 등록합니다.

```csharp
// 예: App.xaml.cs (WPF Startup) 또는 Program.cs (Console/Web Startup)
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

    #if DEBUG
    // 로그가 저장될 베이스 디렉터리 경로 설정
    AiAgent.Diagnostics.AiDebugLogger.BasePath = AppDomain.CurrentDomain.BaseDirectory;

    // 파이프라인 워커 시작 및 진단 옵저버 등록
    AiAgent.Diagnostics.AiDiagnosticChannels.Start();
    AiAgent.Diagnostics.AiDiagnosticObserver.Register();
    #endif
}
```

### 2. 잔여 로그 플러시 및 리스너 해제 (앱 Shutdown)
애플리케이션이 종료될 때 메모리 버퍼/큐에 대기 중인 모든 잔여 로그가 디스크에 유실 없이 쓰이도록 보장(Graceful Shutdown)합니다.

```csharp
protected override void OnExit(ExitEventArgs e)
{
    #if DEBUG
    try
    {
        AiAgent.Diagnostics.AiDiagnosticObserver.Unregister();
        // 비동기 채널을 닫고, 남은 로그 쓰기 작업 완료 시까지 동기 대기
        AiAgent.Diagnostics.AiDiagnosticChannels.StopAsync().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"Failed to stop AI debug pipeline: {ex.Message}");
    }
    #endif
    base.OnExit(e);
}
```

---

## 💻 사용 방법

### 방법 A. 비침투적 이벤트 발행 (추천: 도메인 로직 코드용)
비즈니스 로직 소스 코드 내부에서는 진단 라이브러리를 직접 참조하지 않고, .NET 표준 API를 활용해 정보를 던집니다.

```csharp
using System.Diagnostics;

public class ReportGenerator
{
    // 클래스 내부에 고유 진단 소스를 선언합니다.
    private static readonly DiagnosticSource _diagnosticSource = new DiagnosticListener("ArqaStatic.ReportGenerator");

    public void Generate(string path, MyData data)
    {
        // 1. 일반 로그 성격의 이벤트 발행
        _diagnosticSource.Write("ReportStarted", new { Message = $"Generating report to {path}", Level = "INFO" });

        // 2. 대용량 객체 덤프 및 스냅샷 이벤트 발행
        _diagnosticSource.Write("InputDataSnapshot", new { 
            Label = "InputData", 
            State = data, 
            Level = "SNAPSHOT", 
            Scope = "Report" 
        });

        // 3. XML 등 대량 텍스트 덤프 이벤트 발행 (10KB 이상 시 자동 분리 저장됨)
        string xmlContent = "<root>...</root>";
        _diagnosticSource.Write("XmlDump", new { 
            Label = "OutputXml", 
            Xml = xmlContent, 
            Scope = "XML_PROCESS", 
            Level = "DUMP" 
        });
    }
}
```

### 방법 B. 정적 퍼사드 사용 (편리함: 샌드박스, 테스트 앱, 빠른 디버깅용)
`AiDebugLogger` 클래스를 직접 호출하여 빠르게 로그를 기록합니다.

```csharp
using AiAgent.Diagnostics;

// 1. 단순 텍스트 로깅
AiDebugLogger.Log("작업을 진행 중입니다.", "INFO");

// 2. 성능 측정 스코프 시작 (Stopwatch 연동, Dispose 시 소요시간 자동 기록)
using (AiDebugLogger.BeginScope("DbQueryScope"))
{
    // 3. 임의 객체 직렬화 덤프 (크기에 따라 인라인 저장 혹은 ai_dumps/ 경로 저장 결정됨)
    var queryResult = new { Count = 120, Query = "SELECT * FROM Users" };
    AiDebugLogger.Dump("UserQueryResults", queryResult);
}

// 4. 명시적인 상태 스냅샷 저장 (AI 에이전트 리하이드레이션 검증용)
AiDebugLogger.SaveSnapshot("CurrentSystemState", myStateObject);
```

---

## 🤖 AI 에이전트를 위한 디버깅 가이드 (Prompting)

AI 에이전트와 페어 프로그래밍을 하거나 분석을 시킬 때, 아래 지침을 복사하여 전달하면 효과적으로 문제의 원인을 추적합니다.

> "이 프로젝트에는 `AiAgent.Diagnostics` 관찰(Observability) 파이프라인이 탑재되어 있습니다. 
> 
> **디버깅 프로토콜:**
> 1. 기능 테스트나 예외 발생 시, 프로젝트 루트의 `ai_debug.jsonl` 파일에서 오류 및 관련 이벤트를 확인하세요.
> 2. `ai_debug.jsonl`에 기록된 로그에는 호출 스택 추적을 위해 `caller` 필드(파일명, 라인 수, 메서드명)가 구조화되어 제공됩니다.
> 3. 직렬화된 데이터나 거대한 XML 조각은 외부 파일로 분리되어 `blobPath` 필드에 명시되고 `ai_dumps/` 디렉터리에 저장됩니다. 이 경로의 파일을 읽어 변환 과정을 분석하세요."
