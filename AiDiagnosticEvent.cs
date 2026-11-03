using System;

namespace AiAgent.Diagnostics
{
    /// <summary>
    /// AI 진단 로거에서 발생하는 단일 로그 또는 덤프 이벤트의 메타데이터와 페이로드를 담는 모델 클래스입니다.
    /// </summary>
    public class AiDiagnosticEvent
    {
        /// <summary>
        /// 이벤트가 발생한 로컬 시각을 가져오거나 설정합니다.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>
        /// 이벤트의 심각도 또는 유형 수준(예: INFO, DUMP, SNAPSHOT)을 가져오거나 설정합니다.
        /// </summary>
        public string Level { get; set; } = "INFO";

        /// <summary>
        /// 진단 이벤트 메시지를 가져오거나 설정합니다.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 이벤트가 속한 논리적 실행 범위 또는 모듈 이름을 가져오거나 설정합니다.
        /// </summary>
        public string? Scope { get; set; }

        /// <summary>
        /// 범위 측정 시 작업 소요 시간(밀리초)을 가져오거나 설정합니다.
        /// </summary>
        public long? DurationMs { get; set; }
        
        /// <summary>
        /// 로그를 호출한 소스 파일의 경로를 가져오거나 설정합니다.
        /// </summary>
        public string? CallerFile { get; set; }

        /// <summary>
        /// 로그를 호출한 소스 파일의 줄 번호를 가져오거나 설정합니다.
        /// </summary>
        public int CallerLine { get; set; }

        /// <summary>
        /// 로그를 호출한 메서드 또는 속성 이름을 가져오거나 설정합니다.
        /// </summary>
        public string? CallerMethod { get; set; }
        
        /// <summary>
        /// 이벤트에 첨부된 덤프 또는 상태 페이로드 객체를 가져오거나 설정합니다.
        /// </summary>
        public object? Payload { get; set; }

        /// <summary>
        /// 페이로드 데이터를 식별하기 위한 레이블을 가져오거나 설정합니다.
        /// </summary>
        public string? PayloadLabel { get; set; }
    }
}
