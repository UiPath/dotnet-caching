namespace UiPath.Caching;

/// <summary>An entry a peer refresh read, holding the serialized payload until a read asks for the value, so a copy no one reads before the next write stays one buffer. The first read pays the deserialization.</summary>
/// <remarks><see cref="ICacheEntry.Found"/> and <see cref="Value"/> deserialize, once, under a lock of the payload's own that runs only the serializer; a payload that fails to deserialize, or reads as a value the cache would not keep, is not found, and the cache's local reads drop it and read the inner tier. <see cref="Expiration"/> does not deserialize.</remarks>
internal sealed class DeferredEntry<T> : ICacheEntry<T?>, IReadTracked, IDeferredEntry
{
    private readonly DeferredValue _value;
    private int _read;

    public DeferredEntry(byte[] payload, ISerializerProxy<byte[]> serializer, bool keepsDefault, Action<Exception>? failed, DateTimeOffset expiration)
        : this(new DeferredValue(payload, serializer, keepsDefault, failed), expiration, null)
    {
    }

    private DeferredEntry(DeferredValue value, DateTimeOffset expiration, IDictionary<string, string?>? metadata)
    {
        _value = value;
        Expiration = expiration;
        Metadata = metadata;
    }

    public T? Value => _value.Get(out var value) ? value : default;

    public DateTimeOffset Expiration { get; }

    public IDictionary<string, string?>? Metadata { get; }

    object? ICacheEntry.Value => Value;

    bool ICacheEntry.Found => Expiration > DateTimeOffset.MinValue && _value.Get(out _);

    int IReadTracked.LastRead => Volatile.Read(ref _read);

    /// <summary>Whether the payload has been deserialized, or failed to.</summary>
    internal bool Materialized => _value.Materialized;

    void IReadTracked.MarkRead(int nowSeconds)
    {
        if (Volatile.Read(ref _read) != nowSeconds)
        {
            Volatile.Write(ref _read, nowSeconds);
        }
    }

    bool IDeferredEntry.Holds(out bool first, out bool threw) => _value.Holds(out first, out threw);

    /// <summary>Shares the payload, deserialized or not.</summary>
    public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
        new DeferredEntry<T>(_value, expiration ?? DateTimeOffset.MaxValue, metadata);

    private sealed class DeferredValue(byte[] payload, ISerializerProxy<byte[]> serializer, bool keepsDefault, Action<Exception>? failed)
    {
        private const int Pending = 0;
        private const int Present = 1;
        private const int Absent = 2;

        private readonly Lock _gate = new();
        private byte[]? _payload = payload;
        private T? _value;
        private Exception? _error;
        private int _state;
        private int _reported;

        public bool Materialized => Volatile.Read(ref _state) != Pending;

        public bool Get(out T? value)
        {
            var state = Volatile.Read(ref _state);
            if (state == Pending)
            {
                state = Materialize();
            }

            value = _value;
            return state == Present;
        }

        public bool Holds(out bool first, out bool threw)
        {
            var present = Get(out _);
            first = !present && Interlocked.Exchange(ref _reported, 1) == 0;
            threw = _error is not null;
            return present;
        }

        private int Materialize()
        {
            int state;
            Exception? error = null;
            using (_gate.EnterScope())
            {
                if (_state != Pending)
                {
                    return _state;
                }

                state = Absent;
                try
                {
                    var value = serializer.Deserialize<T>(_payload);
                    if (keepsDefault || !CacheValueHelpers.IsDefault(value))
                    {
                        _value = value;
                        state = Present;
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                    _error = ex;
                }
                finally
                {
                    _payload = null;
                    // After the value: a reader that sees the state sees the value.
                    Volatile.Write(ref _state, state);
                }
            }

            // Outside the lock: a logger is not held up by, nor holds up, the other first readers.
            if (error is not null)
            {
                failed?.Invoke(error);
            }

            return state;
        }
    }
}
