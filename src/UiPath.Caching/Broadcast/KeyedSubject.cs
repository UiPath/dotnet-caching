using System.Collections.Concurrent;
using System.Diagnostics;

namespace UiPath.Caching.Broadcast;

internal sealed partial class KeyedSubject<T> : IEventSubject<T> where T : IEvent
{
    // Per key, its observers as an array replaced whole on every change under the keyed lock: readers take the array as it is. One token per entry is the norm, and a dictionary per key weighed more than the entry.
    private readonly ConcurrentDictionary<string, IObserver<T>[]> _keyedObservers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<IObserver<T>, byte> _broadcastObservers = new();
    private readonly Lock _keyedLock = new();

    private readonly ILogger _logger;
    private readonly TimeSpan _slowObserverThreshold;
    private volatile bool _completed;
    // Replaced whole under the keyed lock, so a dispatch reads it without allocating.
    private volatile IObserver<T>[] _firstObservers = [];

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
            lock (_keyedLock)
            {
                _keyedObservers.AddOrUpdate(
                    keyed.Key,
                    static (_, added) => [added],
                    static (_, current, added) => Array.IndexOf(current, added) >= 0 ? current : [.. current, added],
                    observer);
            }

            return CompletedMeanwhile(keyed.Key, observer) ? Disposable.Empty : new Subscription(this, keyed.Key, observer);
        }

        if (observer is IFirstObserver<T>)
        {
            lock (_keyedLock)
            {
                if (Array.IndexOf(_firstObservers, observer) < 0)
                {
                    _firstObservers = [.. _firstObservers, observer];
                }
            }
        }
        else
        {
            _broadcastObservers.TryAdd(observer, 0);
        }

        return CompletedMeanwhile(null, observer) ? Disposable.Empty : new Subscription(this, null, observer);
    }

    public void OnNext(T value)
    {
        if (_completed)
        {
            return;
        }

        if (value.Type == KnownEventTypes.TopicInvalidated)
        {
            Invalidate(MissedEventsReason.Lost);
            return;
        }

        foreach (var first in _firstObservers)
        {
            SafeOnNext(first, value);
        }

        // Looked up after the first observers: a token that subscribes before they return is told.
        var key = value.Key;
        if (key != null && _keyedObservers.TryGetValue(key, out var observers))
        {
            foreach (var observer in observers)
            {
                SafeOnNext(observer, value);
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

        // Taken and cleared in one step under the keyed lock, so the observers taken are this call's to complete. One registered before is taken here; one registered after sees the flag and completes itself.
        KeyValuePair<string, IObserver<T>[]>[] keyed;
        lock (_keyedLock)
        {
            keyed = _keyedObservers.ToArray();
            _keyedObservers.Clear();
        }

        foreach (var (_, observers) in keyed)
        {
            foreach (var observer in observers)
            {
                SafeOnCompleted(observer);
            }
        }

        foreach (var first in _firstObservers)
        {
            CompleteIfRemoved(null, first);
        }

        foreach (var kvp in _broadcastObservers)
        {
            CompleteIfRemoved(null, kvp.Key);
        }
    }

    public void Dispose() => OnCompleted();

    /// <summary>Expires every <see cref="IMissedEventsObserver"/> observer and keeps it subscribed.</summary>
    public void Invalidate(MissedEventsReason reason)
    {
        if (_completed)
        {
            return;
        }

        foreach (var first in _firstObservers)
        {
            SafeInvalidate(first, reason);
        }

        foreach (var observers in _keyedObservers.Values)
        {
            foreach (var observer in observers)
            {
                SafeInvalidate(observer, reason);
            }
        }

        foreach (var kvp in _broadcastObservers)
        {
            SafeInvalidate(kvp.Key, reason);
        }
    }

    private bool CompletedMeanwhile(string? key, IObserver<T> observer)
    {
        if (!_completed)
        {
            return false;
        }

        CompleteIfRemoved(key, observer);
        return true;
    }

    private void CompleteIfRemoved(string? key, IObserver<T> observer)
    {
        if (TryRemove(key, observer))
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

    /// <summary>True when the observer was subscribed, so whoever removes it owns its completion.</summary>
    private bool TryRemove(string? key, IObserver<T> observer)
    {
        if (key is null)
        {
            return observer is IFirstObserver<T> ? TryRemoveFirst(observer) : _broadcastObservers.TryRemove(observer, out _);
        }

        lock (_keyedLock)
        {
            if (!_keyedObservers.TryGetValue(key, out var current))
            {
                return false;
            }

            var index = Array.IndexOf(current, observer);
            if (index < 0)
            {
                return false;
            }

            if (current.Length == 1)
            {
                _keyedObservers.TryRemove(key, out _);
            }
            else
            {
                var rest = new IObserver<T>[current.Length - 1];
                Array.Copy(current, rest, index);
                Array.Copy(current, index + 1, rest, index, rest.Length - index);
                _keyedObservers[key] = rest;
            }

            return true;
        }
    }

    private bool TryRemoveFirst(IObserver<T> observer)
    {
        lock (_keyedLock)
        {
            var current = _firstObservers;
            var index = Array.IndexOf(current, observer);
            if (index < 0)
            {
                return false;
            }

            _firstObservers = [.. current[..index], .. current[(index + 1)..]];
            return true;
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
            Interlocked.Exchange(ref _subject, null)?.TryRemove(key, observer);
        }
    }
}
