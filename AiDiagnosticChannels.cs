using System;
using System.IO;
using System.Text;
using System.Text.Json;
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
        private static readonly Channel<AiDiagnosticEvent> _channel = Channel.CreateUnbounded<AiDiagnosticEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true
            });

        private static Task? _processingTask;
        private static CancellationTokenSource? _cts;
        private static bool _isStarted = false;

        /// <summary>
        /// 백그라운드 진단 이벤트 소비 워커 태스크를 시작합니다.
        /// </summary>
        public static void Start()
        {
            lock (_channel)
            {
                if (_isStarted) return;
                _isStarted = true;
                _cts = new CancellationTokenSource();
                _processingTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
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
        /// 채널 입력을 닫고 잔여 이벤트를 처리한 후 백그라운드 워커를 안전하게 중지합니다.
        /// </summary>
        /// <returns>비동기 중지 작업을 나타내는 <see cref="Task"/>입니다.</returns>
        public static async Task StopAsync()
        {
            CancellationTokenSource? localCts = null;
            Task? localTask = null;

            lock (_channel)
            {
                if (!_isStarted) return;
                _isStarted = false;
                _channel.Writer.Complete();
                localCts = _cts;
                localTask = _processingTask;
            }

            if (localCts != null)
            {
                localCts.Cancel();
            }

            if (localTask != null)
            {
                try
                {
                    // Wait at most 300ms for remaining items to be drained from the channel
                    await Task.WhenAny(localTask, Task.Delay(300)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when token is cancelled
                }
                catch (Exception ex)
                {
                    // Safety check to avoid crashing application shutdown
                    System.Diagnostics.Debug.WriteLine($"[AiDiagnosticChannels] Error stopping background task: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 취소 요청이 발생할 때까지 채널에서 이벤트를 지속적으로 읽어와 처리합니다.
        /// </summary>
        /// <param name="ct">작업 취소 토큰입니다.</param>
        /// <returns>비동기 큐 처리 작업을 나타내는 <see cref="Task"/>입니다.</returns>
        private static async Task ProcessQueueAsync(CancellationToken ct)
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var ev))
                {
                    try
                    {
                        await ProcessEventAsync(ev).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AiDiagnosticChannels] Error processing event: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 단일 진단 이벤트를 직렬화하여 로그 파일에 기록하고, 10KB를 초과하는 대용량 페이로드는 별도 덤프 파일로 분리 저장합니다.
        /// </summary>
        /// <param name="ev">처리할 진단 이벤트입니다.</param>
        /// <returns>비동기 이벤트 처리 작업을 나타내는 <see cref="Task"/>입니다.</returns>
        private static async Task ProcessEventAsync(AiDiagnosticEvent ev)
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
                    if (str.TrimStart().StartsWith("<"))
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
                        rawContent = JsonSerializer.Serialize(ev.Payload, new JsonSerializerOptions { WriteIndented = true });
                    }
                    catch (Exception ex)
                    {
                        rawContent = $"Serialization Failed: {ex.Message}";
                    }
                }

                // 10KB threshold
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

                        blobPath = fullPath;
                        message = $"[Offloaded] {label} ({rawContent.Length} chars). Saved to file: {fullPath}";
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

            string jsonLine = JsonSerializer.Serialize(record);

            try
            {
                string logFilePath = AiDebugLogger.LogFilePath;
                string? dir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.AppendAllTextAsync(logFilePath, jsonLine + Environment.NewLine, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiDiagnosticChannels] Error writing to log file: {ex.Message}");
            }
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
