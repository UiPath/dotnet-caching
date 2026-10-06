using System.Collections.Concurrent;
using System.Diagnostics;

namespace UiPath.Caching.Broadcast;

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

        // Completion may run while this registers: checked again after, and whoever removes the observer completes it, once.
        if (observer is IKeyedObserver<T> keyed)
        {
            ConcurrentDictionary<IObserver<T>, byte> inner;
            lock (_keyedLock)
            {
                inner = _keyedObservers.GetOrAdd(keyed.Key, _ => new ConcurrentDictionary<IObserver<T>, byte>());
                inner.TryAdd(observer, 0);
            }

            return CompletedMeanwhile(inner, observer) ? Disposable.Empty : new Subscription(this, keyed.Key, observer);
        }

        _broadcastObservers.TryAdd(observer, 0);
        return CompletedMeanwhile(_broadcastObservers, observer) ? Disposable.Empty : new Subscription(this, null, observer);
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

    public void OnCompleted()
    {
        _completed = true;

        foreach (var inner in _keyedObservers.Values)
        {
            foreach (var kvp in inner)
            {
                CompleteIfRemoved(inner, kvp.Key);
            }
        }

        foreach (var kvp in _broadcastObservers)
        {
            CompleteIfRemoved(_broadcastObservers, kvp.Key);
        }

        _keyedObservers.Clear();
    }

    public void Dispose() => OnCompleted();

    /// <summary>Expires every <see cref="IMissedEventsObserver"/> observer and keeps it subscribed.</summary>
    public void Invalidate(MissedEventsReason reason)
    {
        if (_completed)
        {
            return;
        }

        foreach (var inner in _keyedObservers.Values)
        {
            foreach (var kvp in inner)
            {
                SafeInvalidate(kvp.Key, reason);
            }
        }

        foreach (var kvp in _broadcastObservers)
        {
            SafeInvalidate(kvp.Key, reason);
        }
    }

    private bool CompletedMeanwhile(ConcurrentDictionary<IObserver<T>, byte> observers, IObserver<T> observer)
    {
        if (!_completed)
        {
            return false;
        }

        CompleteIfRemoved(observers, observer);
        return true;
    }

    private void CompleteIfRemoved(ConcurrentDictionary<IObserver<T>, byte> observers, IObserver<T> observer)
    {
        if (observers.TryRemove(observer, out _))
        {
            SafeOnCompleted(observer);
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

    private void SafeInvalidate(IObserver<T> observer, MissedEventsReason reason)
    {
        if (observer is not IMissedEventsObserver missed)
        {
            return;
        }

        try
        {
            missed.OnEventsMissed(reason);
        }
        catch (Exception ex)
        {
            LogObserverInvalidateFailed(ex);
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Observer threw in OnNext for event {EventId}; continuing with remaining observers.")]
    private partial void LogObserverOnNextFailed(Exception ex, string? eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Observer threw in OnCompleted; continuing.")]
    private partial void LogObserverOnCompletedFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Observer threw in OnEventsMissed; continuing.")]
    private partial void LogObserverInvalidateFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Slow observer {Observer} took {ElapsedMs} ms in OnNext for event {EventId}.")]
    private partial void LogObserverSlow(string? observer, double elapsedMs, string? eventId);

    private sealed class Subscription(KeyedSubject<T> subject, string? key, IObserver<T> observer) : IDisposable
    {
        private KeyedSubject<T>? _subject = subject;

        public void Dispose()
        {
            Interlocked.Exchange(ref _subject, null)?.Unsubscribe(key, observer);
        }
    }
}
