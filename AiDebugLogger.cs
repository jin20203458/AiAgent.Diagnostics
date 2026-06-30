using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace AiAgent.Diagnostics
{
    public static class AiDebugLogger
    {
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
            LogFilePath = Path.Combine(_basePath, "ai_debug.jsonl");
            DumpsDirectoryPath = Path.Combine(_basePath, "ai_dumps");
        }

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

                EnqueueEvent("START", $"Scope started: {_scopeName}", _scopeName, null, null, null, _filePath, _lineNumber, _memberName);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _stopwatch.Stop();
                _disposed = true;
                EnqueueEvent("END", $"Scope ended: {_scopeName}", _scopeName, _stopwatch.ElapsedMilliseconds, null, null, _filePath, _lineNumber, _memberName);
            }
        }

        private class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new NullDisposable();
            public void Dispose() { }
        }
    }
}
