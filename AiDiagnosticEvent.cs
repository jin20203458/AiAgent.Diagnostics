using System;

namespace AiAgent.Diagnostics
{
    public class AiDiagnosticEvent
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Level { get; set; } = "INFO";
        public string Message { get; set; } = string.Empty;
        public string? Scope { get; set; }
        public long? DurationMs { get; set; }
        
        public string? CallerFile { get; set; }
        public int CallerLine { get; set; }
        public string? CallerMethod { get; set; }
        
        public object? Payload { get; set; }
        public string? PayloadLabel { get; set; }
    }
}
