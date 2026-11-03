using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AiAgent.Diagnostics
{
    /// <summary>
    /// <see cref="Channel{T}"/> 기반으로 진단 이벤트를 비동기 큐잉하고 JSONL 파일 및 외부 덤프 파일로 직렬화하여 기록하는 정적 클래스입니다.
    /// </summary>
    public static class AiDiagnosticChannels
    {
        /// <summary>
        /// 동기화를 위한 내부 잠금 객체입니다.
        /// </summary>
        private static readonly object _syncRoot = new object();

        /// <summary>
        /// 순환 참조를 무시하고 들여쓰기를 적용하는 덤프용 JSON 직렬화 옵션입니다.
        /// </summary>
        private static readonly JsonSerializerOptions _indentedJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReferenceHandler = ReferenceHandler.IgnoreCycles
        };

        /// <summary>
        /// 순환 참조를 무시하고 단일 라인 JSONL 레코드를 생성하는 직렬화 옵션입니다.
        /// </summary>
        private static readonly JsonSerializerOptions _lineJsonOptions = new JsonSerializerOptions
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles
        };

        /// <summary>
        /// 진단 이벤트를 비동기적으로 버퍼링하는 무제한 단일 소비자 채널입니다.
        /// </summary>
        private static Channel<AiDiagnosticEvent> _channel = CreateChannel();

        /// <summary>
        /// 채널 큐에서 이벤트를 읽어 파일로 기록하는 백그라운드 처리 태스크입니다.
        /// </summary>
        private static Task? _processingTask;

        /// <summary>
        /// 백그라운드 큐 소비 루프를 강제 중지시키기 위한 취소 토큰 소스입니다.
        /// </summary>
        private static CancellationTokenSource? _cts;

        /// <summary>
        /// 채널 백그라운드 워커가 시작되었는지 여부를 나타냅니다.
        /// </summary>
        private static bool _isStarted = false;

        /// <summary>
        /// 단일 소비자 무제한 채널 인스턴스를 새로 생성합니다.
        /// </summary>
        /// <returns>생성된 <see cref="Channel{AiDiagnosticEvent}"/> 인스턴스입니다.</returns>
        private static Channel<AiDiagnosticEvent> CreateChannel()
        {
            return Channel.CreateUnbounded<AiDiagnosticEvent>(
                new UnboundedChannelOptions
                {
                    SingleReader = true
                });
        }

        /// <summary>
        /// 이전 실행 세션의 로그 파일을 회전(<c>ai_debug.prev.jsonl</c>)하고 백그라운드 진단 이벤트 소비 워커 태스크를 시작합니다.
        /// </summary>
        public static void Start()
        {
            lock (_syncRoot)
            {
                if (_isStarted) return;
                _isStarted = true;

                if (_channel.Reader.Completion.IsCompleted)
                {
                    _channel = CreateChannel();
                }

                RotatePreviousSessionLog();

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _processingTask = Task.Run(() => ProcessQueueAsync(token));
            }
        }

        /// <summary>
        /// 기존 <c>ai_debug.jsonl</c> 파일이 존재하고 내용이 있으면 <c>ai_debug.prev.jsonl</c>로 백업한 뒤 현재 세션 로그 파일을 초기화합니다.
        /// </summary>
        private static void RotatePreviousSessionLog()
        {
            try
            {
                string logFilePath = AiDebugLogger.LogFilePath;
                string? dir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(logFilePath))
                {
                    var fileInfo = new FileInfo(logFilePath);
                    if (fileInfo.Length > 0)
                    {
                        string prevFilePath = Path.Combine(dir ?? string.Empty, "ai_debug.prev.jsonl");
                        File.Copy(logFilePath, prevFilePath, overwrite: true);
                    }
                    File.WriteAllText(logFilePath, string.Empty, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiDiagnosticChannels] Error rotating session log: {ex.Message}");
            }
        }

        /// <summary>
        /// 진단 이벤트를 비동기 채널 큐에 추가합니다.
        /// </summary>
        /// <param name="ev">큐에 추가할 <see cref="AiDiagnosticEvent"/> 인스턴스입니다.</param>
        public static void Enqueue(AiDiagnosticEvent ev)
        {
            _channel.Writer.TryWrite(ev);
        }

        /// <summary>
        /// 채널 입력을 닫고 대기 중인 잔여 이벤트를 모두 파일에 기록한 후 백그라운드 워커를 안전하게 중지합니다.
        /// </summary>
        /// <returns>비동기 중지 작업을 나타내는 <see cref="Task"/>입니다.</returns>
        public static async Task StopAsync()
        {
            CancellationTokenSource? localCts = null;
            Task? localTask = null;

            lock (_syncRoot)
            {
                if (!_isStarted) return;
                _isStarted = false;
                _channel.Writer.TryComplete();
                localCts = _cts;
                localTask = _processingTask;
                _cts = null;
                _processingTask = null;
            }

            if (localTask != null)
            {
                try
                {
                    // 큐에 남은 모든 이벤트(크래시 덤프 포함)가 디스크에 기록될 때까지 최대 1000ms 대기
                    Task completedTask = await Task.WhenAny(localTask, Task.Delay(1000)).ConfigureAwait(false);
                    if (completedTask != localTask && localCts != null)
                    {
                        localCts.Cancel();
                    }
                }
                catch (OperationCanceledException)
                {
                    // 타임아웃 취소 시 정상 종료 처리
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AiDiagnosticChannels] Error stopping background task: {ex.Message}");
                }
            }

            localCts?.Dispose();
        }

        /// <summary>
        /// 채널이 닫히거나 취소 요청이 발생할 때까지 이벤트를 지속적으로 읽어와 파일 스트림에 기록합니다.
        /// </summary>
        /// <param name="ct">작업 취소 토큰입니다.</param>
        /// <returns>비동기 큐 처리 작업을 나타내는 <see cref="Task"/>입니다.</returns>
        private static async Task ProcessQueueAsync(CancellationToken ct)
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    string logFilePath = AiDebugLogger.LogFilePath;
                    string? dir = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    using var stream = new FileStream(
                        logFilePath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        bufferSize: 4096,
                        useAsync: true);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                    while (_channel.Reader.TryRead(out var ev))
                    {
                        try
                        {
                            string jsonLine = await BuildJsonLineAsync(ev).ConfigureAwait(false);
                            await writer.WriteLineAsync(jsonLine).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AiDiagnosticChannels] Error processing event: {ex.Message}");
                        }
                    }

                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 강제 종료 타임아웃 시 루프 종료
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiDiagnosticChannels] Queue worker error: {ex.Message}");
            }
        }

        /// <summary>
        /// 단일 진단 이벤트를 직렬화하여 JSONL 문자열을 생성하고, 10KB를 초과하는 대용량 페이로드는 별도 덤프 파일로 분리 저장합니다.
        /// </summary>
        /// <param name="ev">처리할 진단 이벤트입니다.</param>
        /// <returns>직렬화된 단일 라인 JSON 문자열을 반환하는 <see cref="Task{String}"/>입니다.</returns>
        private static async Task<string> BuildJsonLineAsync(AiDiagnosticEvent ev)
        {
            string? blobPath = null;
            string? inlineData = null;
            string message = ev.Message;

            if (ev.Payload != null)
            {
                string rawContent = "";
                string fileExt = ".json";

                if (ev.Payload is string str)
                {
                    rawContent = str;
                    if (str.TrimStart().StartsWith("<", StringComparison.Ordinal))
                    {
                        fileExt = ".xml";
                        try
                        {
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
                        rawContent = JsonSerializer.Serialize(ev.Payload, _indentedJsonOptions);
                    }
                    catch (Exception ex)
                    {
                        rawContent = $"Serialization Failed ({ev.Payload.GetType().Name}): {ex.Message}";
                    }
                }

                // 10KB 임계치 초과 시 외부 파일(ai_dumps/)로 분리 저장
                const int ThresholdBytes = 10 * 1024;
                if (rawContent.Length > ThresholdBytes)
                {
                    try
                    {
                        string dumpsPath = AiDebugLogger.DumpsDirectoryPath;
                        if (!Directory.Exists(dumpsPath))
                        {
                            Directory.CreateDirectory(dumpsPath);
                        }

                        string label = ev.PayloadLabel ?? "Dump";
                        string fileName = $"dump_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{SanitizeFileName(label)}{fileExt}";
                        string fullPath = Path.Combine(dumpsPath, fileName);

                        await File.WriteAllTextAsync(fullPath, rawContent, Encoding.UTF8).ConfigureAwait(false);

                        string preview = rawContent.Length > 160
                            ? rawContent.Substring(0, 160).Replace("\r", " ").Replace("\n", " ") + "..."
                            : rawContent.Replace("\r", " ").Replace("\n", " ");

                        blobPath = fullPath;
                        message = $"[Offloaded] {label} ({rawContent.Length} chars). Saved to file: {fullPath} | Preview: {preview}";
                    }
                    catch (Exception ex)
                    {
                        message = $"Failed to offload dump for '{ev.PayloadLabel}': {ex.Message}";
                    }
                }
                else
                {
                    inlineData = rawContent;
                }
            }

            var record = new
            {
                timestamp = ev.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffK"),
                level = ev.Level,
                message = message,
                scope = ev.Scope,
                durationMs = ev.DurationMs,
                caller = new
                {
                    file = ev.CallerFile != null ? Path.GetFileName(ev.CallerFile) : "",
                    line = ev.CallerLine,
                    method = ev.CallerMethod ?? ""
                },
                blobPath = blobPath,
                data = inlineData
            };

            return JsonSerializer.Serialize(record, _lineJsonOptions);
        }

        /// <summary>
        /// 파일 이름에 사용할 수 없는 문자를 밑줄(<c>_</c>)로 치환합니다.
        /// </summary>
        /// <param name="name">원본 파일 이름 문자열입니다.</param>
        /// <returns>안전하게 정제된 파일 이름 문자열입니다.</returns>
        private static string SanitizeFileName(string name)
        {
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);
            return Regex.Replace(name, invalidRegStr, "_");
        }
    }
}
