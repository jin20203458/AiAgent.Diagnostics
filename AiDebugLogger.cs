using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace AiAgent.Diagnostics
{
    public static class AiDebugLogger
    {
        private static readonly object _fileLock = new object();
        private static string _basePath = AppDomain.CurrentDomain.BaseDirectory;
        
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

        public static string LogFilePath { get; private set; }
        public static string DumpsDirectoryPath { get; private set; }
        public static bool IsEnabled { get; set; } = true;

        static AiDebugLogger()
        {
            // 초기 기본 경로는 AppDomain 실행 경로 기준
            LogFilePath = Path.Combine(_basePath, "ai_debug.jsonl");
            DumpsDirectoryPath = Path.Combine(_basePath, "ai_dumps");
        }

        /// <summary>
        /// 일반 디버그 메시지를 기록합니다. (디버그 빌드에서만 수행)
        /// </summary>
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
            LogInternal(level, message, scope, null, null, null, filePath, lineNumber, memberName);
#endif
        }

        /// <summary>
        /// 복잡한 객체나 문자열을 디버깅용으로 기록하며, 크기가 크면 dumps 폴더에 자동 오프로딩합니다.
        /// </summary>
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
                LogInternal("DUMP", $"{label}: null", scope, null, null, null, filePath, lineNumber, memberName);
                return;
            }

            string rawContent = "";
            string fileExt = ".json";

            if (obj is string str)
            {
                rawContent = str;
                // 간단한 XML 판단 규칙
                if (str.TrimStart().StartsWith("<"))
                {
                    fileExt = ".xml";
                    try
                    {
                        // XML 정렬(Pretty Print)을 통해 AI 가독성 극대화
                        var doc = XDocument.Parse(str);
                        rawContent = doc.ToString();
                    }
                    catch { }
                }
            }
            else
            {
                try
                {
                    rawContent = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (Exception ex)
                {
                    rawContent = $"Serialization Failed: {ex.Message}";
                }
            }

            // 스마트 데이터 분리 (10KB 이상일 경우 오프로딩)
            const int ThresholdBytes = 10 * 1024;
            if (rawContent.Length > ThresholdBytes)
            {
                try
                {
                    if (!Directory.Exists(DumpsDirectoryPath))
                    {
                        Directory.CreateDirectory(DumpsDirectoryPath);
                    }

                    string fileName = $"dump_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{SanitizeFileName(label)}{fileExt}";
                    string fullPath = Path.Combine(DumpsDirectoryPath, fileName);
                    
                    File.WriteAllText(fullPath, rawContent, System.Text.Encoding.UTF8);

                    string summary = $"[Offloaded] {label} ({rawContent.Length} chars). Saved to file: {fullPath}";
                    LogInternal("DUMP", summary, scope, null, fullPath, null, filePath, lineNumber, memberName);
                }
                catch (Exception ex)
                {
                    LogInternal("ERROR", $"Failed to offload dump for '{label}': {ex.Message}", scope, null, null, null, filePath, lineNumber, memberName);
                }
            }
            else
            {
                // 크기가 작으면 로그 내에 직접 인라인으로 json 문자열 기록
                LogInternal("DUMP", $"{label} inline value", scope, null, null, rawContent, filePath, lineNumber, memberName);
            }
#endif
        }

        /// <summary>
        /// 특정 실행 구간의 시작과 끝, 소요 시간을 추적합니다.
        /// </summary>
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
        /// AI 에이전트의 상태 재수화(Rehydration) 테스트를 위한 명시적 상태 백업
        /// </summary>
        public static void SaveSnapshot(
            string snapshotId, 
            object state,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
#if DEBUG
            if (!IsEnabled) return;
            Dump($"SNAPSHOT_{snapshotId}", state, "SNAPSHOT", filePath, lineNumber, memberName);
#endif
        }

        private static void LogInternal(
            string level, 
            string message, 
            string? scope, 
            long? durationMs, 
            string? blobPath, 
            string? inlineData,
            string filePath,
            int lineNumber,
            string memberName)
        {
            var record = new
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffK"),
                level = level,
                message = message,
                scope = scope,
                durationMs = durationMs,
                caller = new
                {
                    file = Path.GetFileName(filePath),
                    line = lineNumber,
                    method = memberName
                },
                blobPath = blobPath,
                data = inlineData
            };

            string jsonLine = JsonSerializer.Serialize(record);

            lock (_fileLock)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(LogFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.AppendAllText(LogFilePath, jsonLine + Environment.NewLine, System.Text.Encoding.UTF8);
                }
                catch { }
            }
        }

        private static string SanitizeFileName(string name)
        {
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);
            return Regex.Replace(name, invalidRegStr, "_");
        }

        private class LogScope : IDisposable
        {
            private readonly string _scopeName;
            private readonly string _filePath;
            private readonly int _lineNumber;
            private readonly string _memberName;
            private readonly Stopwatch _stopwatch;
            private bool _disposed = false;

            public LogScope(string scopeName, string filePath, int lineNumber, string memberName)
            {
                _scopeName = scopeName;
                _filePath = filePath;
                _lineNumber = lineNumber;
                _memberName = memberName;
                _stopwatch = Stopwatch.StartNew();

                LogInternal("START", $"Scope started: {_scopeName}", _scopeName, null, null, null, _filePath, _lineNumber, _memberName);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _stopwatch.Stop();
                _disposed = true;
                LogInternal("END", $"Scope ended: {_scopeName}", _scopeName, _stopwatch.ElapsedMilliseconds, null, null, _filePath, _lineNumber, _memberName);
            }
        }

        private class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new NullDisposable();
            public void Dispose() { }
        }
    }
}
