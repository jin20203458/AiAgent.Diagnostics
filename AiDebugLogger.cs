using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace AiAgent.Diagnostics
{
    /// <summary>
    /// 애플리케이션 생명주기 이벤트, 객체 덤프, 실행 범위 측정 및 스냅샷을 비동기 진단 채널에 기록하는 정적 로거 클래스입니다.
    /// </summary>
    public static class AiDebugLogger
    {
        /// <summary>
        /// 로그 및 덤프 파일이 저장될 기본 디렉터리 경로입니다.
        /// </summary>
        private static string _basePath = AppDomain.CurrentDomain.BaseDirectory;
        
        /// <summary>
        /// 로그 및 덤프 저장소의 기본 디렉터리 경로를 가져오거나 설정합니다.
        /// 설정 시 <see cref="LogFilePath"/>와 <see cref="DumpsDirectoryPath"/>가 함께 갱신됩니다.
        /// </summary>
        public static string BasePath
        {
            get => _basePath;
            set
            {
                _basePath = value;
                LogFilePath = Path.Combine(_basePath, "ai_debug.jsonl");
                DumpsDirectoryPath = Path.Combine(_basePath, "ai_dumps");
            }
        }

        /// <summary>
        /// JSON Lines 형식의 디버그 로그 파일(<c>ai_debug.jsonl</c>) 전체 경로를 가져옵니다.
        /// </summary>
        public static string LogFilePath { get; private set; }

        /// <summary>
        /// 대용량 페이로드 덤프 파일이 저장되는 디렉터리(<c>ai_dumps</c>) 경로를 가져옵니다.
        /// </summary>
        public static string DumpsDirectoryPath { get; private set; }

        /// <summary>
        /// 디버그 로깅 활성화 여부를 가져오거나 설정합니다.
        /// </summary>
        public static bool IsEnabled { get; set; } = true;

        /// <summary>
        /// <see cref="AiDebugLogger"/> 클래스의 정적 경로 설정을 초기화합니다.
        /// </summary>
        static AiDebugLogger()
        {
            LogFilePath = Path.Combine(_basePath, "ai_debug.jsonl");
            DumpsDirectoryPath = Path.Combine(_basePath, "ai_dumps");
        }

        /// <summary>
        /// 지정된 메시지와 로그 수준, 범위 정보를 비동기 진단 큐에 기록합니다.
        /// </summary>
        /// <param name="message">기록할 로그 메시지입니다.</param>
        /// <param name="level">로그 수준 문자열입니다 (예: INFO, WARN, ERROR).</param>
        /// <param name="scope">로그가 발생한 논리적 범위 또는 모듈 이름입니다.</param>
        /// <param name="filePath">호출자 소스 파일 경로입니다.</param>
        /// <param name="lineNumber">호출자 소스 라인 번호입니다.</param>
        /// <param name="memberName">호출자 메서드 또는 속성 이름입니다.</param>
        public static void Log(
            string message, 
            string level = "INFO", 
            string? scope = null,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
#if DEBUG
            if (!IsEnabled) return;
            EnqueueEvent(level, message, scope, null, null, null, filePath, lineNumber, memberName);
#endif
        }

        /// <summary>
        /// 지정된 객체를 직렬화하여 진단 덤프 이벤트로 기록합니다.
        /// </summary>
        /// <param name="label">덤프 데이터를 식별하기 위한 레이블입니다.</param>
        /// <param name="obj">덤프할 대상 객체입니다.</param>
        /// <param name="scope">덤프가 발생한 논리적 범위입니다.</param>
        /// <param name="filePath">호출자 소스 파일 경로입니다.</param>
        /// <param name="lineNumber">호출자 소스 라인 번호입니다.</param>
        /// <param name="memberName">호출자 메서드 또는 속성 이름입니다.</param>
        public static void Dump(
            string label, 
            object? obj, 
            string? scope = null,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
#if DEBUG
            if (!IsEnabled) return;
            if (obj == null)
            {
                EnqueueEvent("DUMP", $"{label}: null", scope, null, null, null, filePath, lineNumber, memberName);
                return;
            }

            EnqueueEvent("DUMP", $"{label} value", scope, null, obj, label, filePath, lineNumber, memberName);
#endif
        }

        /// <summary>
        /// 특정 작업 범위의 시작과 종료 시각 및 소요 시간을 자동으로 측정하여 기록하는 범위를 생성합니다.
        /// </summary>
        /// <param name="scopeName">측정할 작업 범위의 이름입니다.</param>
        /// <param name="filePath">호출자 소스 파일 경로입니다.</param>
        /// <param name="lineNumber">호출자 소스 라인 번호입니다.</param>
        /// <param name="memberName">호출자 메서드 또는 속성 이름입니다.</param>
        /// <returns>해제 시 종료 로그와 소요 시간을 기록하는 <see cref="IDisposable"/> 객체입니다.</returns>
        public static IDisposable BeginScope(
            string scopeName, 
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
#if DEBUG
            return new LogScope(scopeName, filePath, lineNumber, memberName);
#else
            return NullDisposable.Instance;
#endif
        }

        /// <summary>
        /// 애플리케이션 상태 스냅샷을 비동기 진단 채널에 저장합니다.
        /// </summary>
        /// <param name="snapshotId">스냅샷 식별자입니다.</param>
        /// <param name="state">저장할 상태 객체입니다.</param>
        /// <param name="filePath">호출자 소스 파일 경로입니다.</param>
        /// <param name="lineNumber">호출자 소스 라인 번호입니다.</param>
        /// <param name="memberName">호출자 메서드 또는 속성 이름입니다.</param>
        public static void SaveSnapshot(
            string snapshotId, 
            object state,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
#if DEBUG
            if (!IsEnabled) return;
            EnqueueEvent("SNAPSHOT", $"SNAPSHOT_{snapshotId}", "SNAPSHOT", null, state, $"SNAPSHOT_{snapshotId}", filePath, lineNumber, memberName);
#endif
        }

        /// <summary>
        /// <see cref="AiDiagnosticEvent"/> 객체를 생성하여 비동기 채널 큐에 추가합니다.
        /// </summary>
        /// <param name="level">로그 수준입니다.</param>
        /// <param name="message">로그 메시지입니다.</param>
        /// <param name="scope">논리적 범위 이름입니다.</param>
        /// <param name="durationMs">실행 소요 시간(밀리초)입니다.</param>
        /// <param name="payload">첨부할 페이로드 객체입니다.</param>
        /// <param name="payloadLabel">페이로드 레이블입니다.</param>
        /// <param name="filePath">호출자 파일 경로입니다.</param>
        /// <param name="lineNumber">호출자 라인 번호입니다.</param>
        /// <param name="memberName">호출자 멤버 이름입니다.</param>
        private static void EnqueueEvent(
            string level, 
            string message, 
            string? scope, 
            long? durationMs, 
            object? payload, 
            string? payloadLabel,
            string filePath,
            int lineNumber,
            string memberName)
        {
            var ev = new AiDiagnosticEvent
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message,
                Scope = scope,
                DurationMs = durationMs,
                CallerFile = filePath,
                CallerLine = lineNumber,
                CallerMethod = memberName,
                Payload = payload,
                PayloadLabel = payloadLabel
            };

            AiDiagnosticChannels.Enqueue(ev);
        }

        /// <summary>
        /// 작업 범위의 시작과 종료 시점 및 소요 시간을 추적하는 내부 <see cref="IDisposable"/> 구현 클래스입니다.
        /// </summary>
        private class LogScope : IDisposable
        {
            private readonly string _scopeName;
            private readonly string _filePath;
            private readonly int _lineNumber;
            private readonly string _memberName;
            private readonly Stopwatch _stopwatch;
            private bool _disposed = false;

            /// <summary>
            /// <see cref="LogScope"/> 인스턴스를 초기화하고 범위 시작 이벤트를 기록합니다.
            /// </summary>
            /// <param name="scopeName">범위 이름입니다.</param>
            /// <param name="filePath">호출자 파일 경로입니다.</param>
            /// <param name="lineNumber">호출자 라인 번호입니다.</param>
            /// <param name="memberName">호출자 멤버 이름입니다.</param>
            public LogScope(string scopeName, string filePath, int lineNumber, string memberName)
            {
                _scopeName = scopeName;
                _filePath = filePath;
                _lineNumber = lineNumber;
                _memberName = memberName;
                _stopwatch = Stopwatch.StartNew();

                EnqueueEvent("START", $"Scope started: {_scopeName}", _scopeName, null, null, null, _filePath, _lineNumber, _memberName);
            }

            /// <summary>
            /// 스톱워치를 중지하고 범위 종료 이벤트 및 경과 시간을 기록합니다.
            /// </summary>
            public void Dispose()
            {
                if (_disposed) return;
                _stopwatch.Stop();
                _disposed = true;
                EnqueueEvent("END", $"Scope ended: {_scopeName}", _scopeName, _stopwatch.ElapsedMilliseconds, null, null, _filePath, _lineNumber, _memberName);
            }
        }

        /// <summary>
        /// Release 빌드에서 할당 없이 반환하기 위한 빈 <see cref="IDisposable"/> 구현 클래스입니다.
        /// </summary>
        private class NullDisposable : IDisposable
        {
            /// <summary>
            /// 싱글톤 인스턴스입니다.
            /// </summary>
            public static readonly NullDisposable Instance = new NullDisposable();

            /// <summary>
            /// 아무 작업도 수행하지 않습니다.
            /// </summary>
            public void Dispose() { }
        }
    }
}
