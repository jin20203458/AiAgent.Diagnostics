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

        public static void Enqueue(AiDiagnosticEvent ev)
        {
            _channel.Writer.TryWrite(ev);
        }

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
                    // Wait for all remaining items to be drained from the channel
                    await localTask;
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

        private static string SanitizeFileName(string name)
        {
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);
            return Regex.Replace(name, invalidRegStr, "_");
        }
    }
}
