using System.Collections.Concurrent;

namespace UiPath.Platform.Caching.Broadcast;

internal sealed class KeyedSubject<T> : IEventSubject<T> where T : IEvent
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<IObserver<T>, byte>> _keyedObservers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<IObserver<T>, byte> _broadcastObservers = new();
    private readonly object _keyedLock = new();
    private volatile bool _completed;

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
                kvp.Key.OnNext(value);
            }
        }

        foreach (var kvp in _broadcastObservers)
        {
            kvp.Key.OnNext(value);
        }
    }

    public void OnCompleted()
    {
        _completed = true;

        foreach (var inner in _keyedObservers.Values)
        {
            foreach (var kvp in inner)
            {
                kvp.Key.OnCompleted();
            }
        }

        foreach (var kvp in _broadcastObservers)
        {
            kvp.Key.OnCompleted();
        }

        _keyedObservers.Clear();
        _broadcastObservers.Clear();
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
}
