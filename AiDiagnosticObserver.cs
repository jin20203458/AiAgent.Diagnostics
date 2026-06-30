using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace AiAgent.Diagnostics
{
    public class AiDiagnosticObserver : IObserver<DiagnosticListener>
    {
        private static IDisposable? _allListenersSubscription;
        private static readonly ConcurrentBag<IDisposable> _subscriptions = new ConcurrentBag<IDisposable>();

        public static void Register()
        {
            if (_allListenersSubscription == null)
            {
                _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(new AiDiagnosticObserver());
            }
        }

        public static void Unregister()
        {
            _allListenersSubscription?.Dispose();
            _allListenersSubscription = null;
            
            while (_subscriptions.TryTake(out var sub))
            {
                sub.Dispose();
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(DiagnosticListener value)
        {
            // Subscribe to listeners starting with "ArqaStatic" or "AiAgent.Diagnostics"
            if (value.Name.StartsWith("ArqaStatic", StringComparison.OrdinalIgnoreCase) ||
                value.Name.StartsWith("AiAgent.Diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                var subscription = value.Subscribe(new InnerEventObserver(value.Name));
                _subscriptions.Add(subscription);
            }
        }

        private class InnerEventObserver : IObserver<System.Collections.Generic.KeyValuePair<string, object?>>
        {
            private readonly string _listenerName;

            public InnerEventObserver(string listenerName)
            {
                _listenerName = listenerName;
            }

            public void OnCompleted() { }

            public void OnError(Exception error) { }

            public void OnNext(System.Collections.Generic.KeyValuePair<string, object?> value)
            {
                try
                {
                    string eventName = value.Key;
                    object? payload = value.Value;

                    if (payload == null) return;

                    var ev = new AiDiagnosticEvent
                    {
                        Timestamp = DateTime.Now,
                        Level = "INFO",
                        Scope = _listenerName
                    };

                    // Extract properties from anonymous or structured payload objects
                    Type type = payload.GetType();
                    
                    ev.Message = TryGetProperty<string>(payload, type, "Message", "message") ?? eventName;
                    ev.Level = TryGetProperty<string>(payload, type, "Level", "level") ?? "INFO";
                    
                    string? customScope = TryGetProperty<string>(payload, type, "Scope", "scope");
                    if (customScope != null)
                    {
                        ev.Scope = customScope;
                    }

                    ev.DurationMs = TryGetProperty<long?>(payload, type, "DurationMs", "durationMs");
                    ev.CallerFile = TryGetProperty<string>(payload, type, "CallerFile", "callerFile");
                    ev.CallerLine = TryGetProperty<int>(payload, type, "CallerLine", "callerLine");
                    ev.CallerMethod = TryGetProperty<string>(payload, type, "CallerMethod", "callerMethod");

                    // Try to find payload and label
                    ev.Payload = TryGetProperty<object>(payload, type, "Payload", "payload", "Xml", "xml", "State", "state", "Obj", "obj");
                    ev.PayloadLabel = TryGetProperty<string>(payload, type, "PayloadLabel", "payloadLabel", "Label", "label", "SnapshotId", "snapshotId");

                    AiDiagnosticChannels.Enqueue(ev);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AiDiagnosticObserver] Error extracting event payload: {ex.Message}");
                }
            }

            private T? TryGetProperty<T>(object target, Type type, params string[] propertyNames)
            {
                foreach (var name in propertyNames)
                {
                    var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (prop != null)
                    {
                        try
                        {
                            object? val = prop.GetValue(target);
                            if (val is T typedVal)
                            {
                                return typedVal;
                            }
                            // Convert type if possible (e.g. int to long or nullables)
                            if (val != null)
                            {
                                return (T)Convert.ChangeType(val, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
                            }
                        }
                        catch { }
                    }
                }
                return default;
            }
        }
    }
}
