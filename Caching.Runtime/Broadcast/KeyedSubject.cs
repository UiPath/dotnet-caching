using System.Collections.Concurrent;
using System.Diagnostics;

namespace UiPath.Platform.Caching.Broadcast;

internal sealed partial class KeyedSubject<T> : IEventSubject<T> where T : IEvent
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<IObserver<T>, byte>> _keyedObservers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<IObserver<T>, byte> _broadcastObservers = new();
    private readonly object _keyedLock = new();
    private readonly ILogger _logger;
    private readonly TimeSpan _slowObserverThreshold;
    private volatile bool _completed;

    public KeyedSubject(ILogger logger, TimeSpan? slowObserverThreshold = null)
    {
        _logger = logger;
        _slowObserverThreshold = slowObserverThreshold ?? TimeSpan.MaxValue;
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        if (_completed)
        {
            observer.OnCompleted();
            return Disposable.Empty;
        }

        if (observer is IKeyedObserver<T> keyed)
        {
            lock (_keyedLock)
            {
                var inner = _keyedObservers.GetOrAdd(keyed.Key, _ => new ConcurrentDictionary<IObserver<T>, byte>());
                inner.TryAdd(observer, 0);
            }
            return new Subscription(this, keyed.Key, observer);
        }

        _broadcastObservers.TryAdd(observer, 0);
        return new Subscription(this, null, observer);
    }

    public void OnNext(T value)
    {
        if (_completed)
        {
            return;
        }

        var key = value.Key;
        if (key != null && _keyedObservers.TryGetValue(key, out var observers))
        {
            foreach (var kvp in observers)
            {
                SafeOnNext(kvp.Key, value);
            }
        }

        foreach (var kvp in _broadcastObservers)
        {
            SafeOnNext(kvp.Key, value);
        }
    }

    private void SafeOnNext(IObserver<T> observer, T value)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            observer.OnNext(value);
        }
        catch (Exception ex)
        {
            LogObserverOnNextFailed(ex, value.Id);
        }
        var elapsed = Stopwatch.GetElapsedTime(start);
        if (elapsed > _slowObserverThreshold)
        {
            LogObserverSlow(observer.GetType().FullName, elapsed.TotalMilliseconds, value.Id);
        }
    }

    public void OnCompleted()
    {
        _completed = true;

        foreach (var inner in _keyedObservers.Values)
        {
            foreach (var kvp in inner)
            {
                SafeOnCompleted(kvp.Key);
            }
        }

        foreach (var kvp in _broadcastObservers)
        {
            SafeOnCompleted(kvp.Key);
        }

        _keyedObservers.Clear();
        _broadcastObservers.Clear();
    }

    private void SafeOnCompleted(IObserver<T> observer)
    {
        try
        {
            observer.OnCompleted();
        }
        catch (Exception ex)
        {
            LogObserverOnCompletedFailed(ex);
        }
    }

    public void Dispose() => OnCompleted();

    private void Unsubscribe(string? key, IObserver<T> observer)
    {
        if (key != null)
        {
            lock (_keyedLock)
            {
                if (_keyedObservers.TryGetValue(key, out var inner))
                {
                    inner.TryRemove(observer, out _);
                    if (inner.IsEmpty)
                    {
                        _keyedObservers.TryRemove(key, out _);
                    }
                }
            }
        }
        else
        {
            _broadcastObservers.TryRemove(observer, out _);
        }
    }

    private sealed class Subscription(KeyedSubject<T> subject, string? key, IObserver<T> observer) : IDisposable
    {
        private KeyedSubject<T>? _subject = subject;

        public void Dispose()
        {
            Interlocked.Exchange(ref _subject, null)?.Unsubscribe(key, observer);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Observer threw in OnNext for event {EventId}; continuing with remaining observers.")]
    private partial void LogObserverOnNextFailed(Exception ex, string? eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Observer threw in OnCompleted; continuing.")]
    private partial void LogObserverOnCompletedFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Slow observer {Observer} took {ElapsedMs} ms in OnNext for event {EventId}.")]
    private partial void LogObserverSlow(string? observer, double elapsedMs, string? eventId);
}
