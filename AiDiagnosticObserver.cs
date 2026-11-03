using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace AiAgent.Diagnostics
{
    /// <summary>
    /// .NET <see cref="DiagnosticListener"/> 스트림을 구독하여 애플리케이션 내부 진단 이벤트를 <see cref="AiDiagnosticChannels"/>로 전달하는 옵저버 클래스입니다.
    /// </summary>
    public class AiDiagnosticObserver : IObserver<DiagnosticListener>
    {
        /// <summary>
        /// 전역 <see cref="DiagnosticListener.AllListeners"/> 구독 핸들입니다.
        /// </summary>
        private static IDisposable? _allListenersSubscription;
        /// <summary>
        /// 활성화된 개별 리스너 구독 핸들들을 보관하는 스레드 안전 컬렉션입니다.
        /// </summary>
        private static readonly ConcurrentBag<IDisposable> _subscriptions = new ConcurrentBag<IDisposable>();

        /// <summary>
        /// 전역 <see cref="DiagnosticListener.AllListeners"/>에 진단 옵저버를 등록합니다.
        /// </summary>
        public static void Register()
        {
            if (_allListenersSubscription == null)
            {
                _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(new AiDiagnosticObserver());
            }
        }

        /// <summary>
        /// 등록된 모든 <see cref="DiagnosticListener"/> 구독을 해제하고 리소스를 정리합니다.
        /// </summary>
        public static void Unregister()
        {
            _allListenersSubscription?.Dispose();
            _allListenersSubscription = null;
            
            while (_subscriptions.TryTake(out var sub))
            {
                sub.Dispose();
            }
        }

        /// <summary>
        /// 스트림 완료 알림을 처리합니다.
        /// </summary>
        public void OnCompleted() { }

        /// <summary>
        /// 스트림 오류 알림을 처리합니다.
        /// </summary>
        /// <param name="error">발생한 예외입니다.</param>
        public void OnError(Exception error) { }

        /// <summary>
        /// 새로운 <see cref="DiagnosticListener"/>가 감지되었을 때 호출되며, 대상 네임스페이스와 일치하면 내부 이벤트 옵저버를 구독합니다.
        /// </summary>
        /// <param name="value">감지된 <see cref="DiagnosticListener"/> 인스턴스입니다.</param>
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

        /// <summary>
        /// 개별 <see cref="DiagnosticListener"/>에서 발행하는 키-값 이벤트 스트림을 수신하여 <see cref="AiDiagnosticEvent"/>로 변환하는 내부 옵저버 클래스입니다.
        /// </summary>
        private class InnerEventObserver : IObserver<System.Collections.Generic.KeyValuePair<string, object?>>
        {
            /// <summary>
            /// 현재 구독 중인 <see cref="DiagnosticListener"/>의 이름입니다.
            /// </summary>
            private readonly string _listenerName;

            /// <summary>
            /// <see cref="InnerEventObserver"/> 클래스의 새 인스턴스를 초기화합니다.
            /// </summary>
            /// <param name="listenerName">구독 중인 리스너의 이름입니다.</param>
            public InnerEventObserver(string listenerName)
            {
                _listenerName = listenerName;
            }

            /// <summary>
            /// 이벤트 스트림 완료 시 호출됩니다.
            /// </summary>
            public void OnCompleted() { }

            /// <summary>
            /// 이벤트 스트림에서 오류 발생 시 호출됩니다.
            /// </summary>
            /// <param name="error">발생한 예외입니다.</param>
            public void OnError(Exception error) { }

            /// <summary>
            /// 수신된 키-값 이벤트 객체에서 리플렉션으로 속성을 추출하여 진단 채널 큐에 적재합니다.
            /// </summary>
            /// <param name="value">이벤트 이름과 페이로드 객체로 구성된 키-값 쌍입니다.</param>
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

            /// <summary>
            /// 대상 객체에서 후보 속성 이름 목록을 순회하며 일치하는 첫 번째 속성 값을 지정된 형식으로 추출합니다.
            /// </summary>
            /// <typeparam name="T">추출할 반환 값의 형식입니다.</typeparam>
            /// <param name="target">속성을 조회할 대상 객체입니다.</param>
            /// <param name="type">대상 객체의 <see cref="Type"/> 정보입니다.</param>
            /// <param name="propertyNames">조회할 후보 속성 이름 배열입니다.</param>
            /// <returns>추출된 속성 값이며, 찾지 못하거나 변환에 실패하면 기본값을 반환합니다.</returns>
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
