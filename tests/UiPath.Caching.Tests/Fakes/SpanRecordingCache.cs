namespace UiPath.Caching.Tests.Fakes;

/// <summary>An <see cref="ICache"/> with the span capability that records the text of each key it is handed there, in <see cref="Calls"/> as the member and the shape of its other arguments.</summary>
internal sealed class SpanRecordingCache : DictionaryCache, ISpanKeyCache
{
    public List<string> SpanKeys { get; } = [];

    public List<(string Tag, string Key)> Calls { get; } = [];

    public bool RecordKeys { get; set; } = true;

    public int LastLength { get; private set; }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        LastLength = cacheKey.Length;
        Record("GetOrAddAsync(state,gen,policy)", cacheKey);
        if (RecordKeys)
        {
            SpanKeys.Add(cacheKey.ToString());
        }

        return default;
    }

    public ValueTask<T?> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        Record("GetAsync(policy)", cacheKey);
        return default;
    }

    public ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        Record("GetCacheEntryAsync(policy)", cacheKey);
        return default;
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        Record("GetOrAddAsync(gen,policy)", cacheKey);
        return default;
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Record("GetOrAddAsync(gen,TimeSpan,policy)", cacheKey);
        return default;
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Record("GetOrAddAsync(gen,DateTimeOffset,policy)", cacheKey);
        return default;
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Record("GetOrAddAsync(state,gen,TimeSpan,policy)", cacheKey);
        return default;
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Record("GetOrAddAsync(state,gen,DateTimeOffset,policy)", cacheKey);
        return default;
    }

    public ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        Record("ContainsAsync()", cacheKey);
        return default;
    }

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        Record("TimeToLiveAsync()", cacheKey);
        return default;
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        Record("ExpireTimeAsync()", cacheKey);
        return default;
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        Record("GetOrAddWithExpirationAsync(gen,policy)", cacheKey);
        return default;
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        Record("GetOrAddWithExpirationAsync(state,gen,policy)", cacheKey);
        return default;
    }

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, CachePolicy? policy, CancellationToken token = default)
    {
        Record("SetAsync(value,policy)", cacheKey);
        return default;
    }

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Record("SetAsync(value,TimeSpan,policy)", cacheKey);
        return default;
    }

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        Record("SetAsync(value,DateTimeOffset,policy)", cacheKey);
        return default;
    }

    private void Record(string tag, Span<char> cacheKey)
    {
        if (RecordKeys)
        {
            Calls.Add((tag, cacheKey.ToString()));
        }
    }
}
