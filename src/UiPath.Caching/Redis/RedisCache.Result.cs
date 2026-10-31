namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache : IResultCache
{
    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
        => GetOrAddResultCoreAsync(cacheKey, GeneratorRecorder<T>.For(generator), PolicyDuration(policy), policy, token);

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        => GetOrAddResultCoreAsync(cacheKey, GeneratorRecorder<T>.For(generator), CallerDuration(expiration), policy, token);

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        => GetOrAddResultCoreAsync(cacheKey, GeneratorRecorder<T>.For(generator), CallerDuration(expiration), policy, token);

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
        => GetOrAddResultCoreAsync(cacheKey, GeneratorRecorder<T>.For(state, generator), PolicyDuration(policy), policy, token);

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        => GetOrAddResultCoreAsync(cacheKey, GeneratorRecorder<T>.For(state, generator), CallerDuration(expiration), policy, token);

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        => GetOrAddResultCoreAsync(cacheKey, GeneratorRecorder<T>.For(state, generator), CallerDuration(expiration), policy, token);

    ValueTask<CacheResult<T>> IResultCache.GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token)
        where T : default
        => GetOrAddResultCoreAsync(new CacheKey(cacheKey), GeneratorRecorder<T>.For(generator), PolicyDuration(policy), policy, token);

    ValueTask<CacheResult<T>> IResultCache.GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token)
        where T : default
        => GetOrAddResultCoreAsync(new CacheKey(cacheKey), GeneratorRecorder<T>.For(generator), CallerDuration(expiration), policy, token);

    ValueTask<CacheResult<T>> IResultCache.GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token)
        where T : default
        => GetOrAddResultCoreAsync(new CacheKey(cacheKey), GeneratorRecorder<T>.For(generator), CallerDuration(expiration), policy, token);

    ValueTask<CacheResult<T>> IResultCache.GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token)
        where T : default
        => GetOrAddResultCoreAsync(new CacheKey(cacheKey), GeneratorRecorder<T>.For(state, generator), PolicyDuration(policy), policy, token);

    ValueTask<CacheResult<T>> IResultCache.GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token)
        where T : default
        => GetOrAddResultCoreAsync(new CacheKey(cacheKey), GeneratorRecorder<T>.For(state, generator), CallerDuration(expiration), policy, token);

    ValueTask<CacheResult<T>> IResultCache.GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token)
        where T : default
        => GetOrAddResultCoreAsync(new CacheKey(cacheKey), GeneratorRecorder<T>.For(state, generator), CallerDuration(expiration), policy, token);

    private static async ValueTask<CacheResult<T>> AttributeAsync<T>(ValueTask<T?> shared, IInFlightRun run, GeneratorRecorder<T> recorder)
    {
        var value = await shared.ConfigureAwait(false);
        return new CacheResult<T>(value, run.Expiration, recorder.Ran);
    }

    private ValueTask<CacheResult<T>> GetOrAddResultCoreAsync<T>(CacheKey cacheKey, GeneratorRecorder<T> recorder, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var shared = ShareGenerationAsync<T, (RedisCache Cache, CacheKey Key, RedisKey RedisKey, GeneratorRecorder<T> Recorder, TimeSpan? FactoryTimeout, TimeSpan Duration)>(
            redisKey,
            (this, cacheKey, redisKey, recorder, (policy ?? DefaultPolicy)?.FactoryTimeout, duration),
            static (args, run) => args.Cache.ReadOrGenerateResultAsync(args.Key, args.RedisKey, args.Recorder, args.FactoryTimeout, args.Duration, run),
            token,
            out var flight);
        return AttributeAsync(shared, flight, recorder);
    }

    /// <summary>The lifetime a write kept, as the deadline to report; none for an entry without expiry.</summary>
    private DateTimeOffset? StoredDeadline(TimeSpan lifetime)
    {
        var deadline = Clock.ToDateTimeOffset(lifetime);
        return deadline == DateTimeOffset.MaxValue ? null : deadline;
    }

    private async ValueTask<T?> ReadOrGenerateResultAsync<T>(CacheKey cacheKey, RedisKey redisKey, GeneratorRecorder<T> recorder, TimeSpan? factoryTimeout, TimeSpan duration, IInFlightRun run)
    {
        var token = run.Token;

        // One read of value and expiration together, shared by every caller of the key.
        var entry = await ReadEntryAsync<T>(cacheKey, token).ConfigureAwait(false);
        if (entry.Found)
        {
            run.Expiration = entry.Expiration == DateTimeOffset.MaxValue ? null : entry.Expiration;
            return entry.Value;
        }

        LogCacheMissed(Logged(cacheKey, redisKey, typeof(T)));
        var ret = await RunGeneratorAsync(cacheKey, recorder.Generator, static (g, ct) => g(ct), factoryTimeout, token).ConfigureAwait(false);
        // Only a value that was written has an expiration to report.
        if ((!IsDefault(ret) || _cacheNullValues) && await SetInternalAsync(redisKey, ret, duration, token).ConfigureAwait(false))
        {
            run.Expiration = StoredDeadline(duration);
        }

        return ret;
    }

    /// <summary>Where a read script is unavailable (Redis before 7), a <c>GET</c> with <c>PTTL</c> in one round trip, which allocates far less than the transaction the entry read falls back to.</summary>
    private ValueTask<ICacheEntry<T?>> ReadEntryAsync<T>(CacheKey cacheKey, CancellationToken token) =>
        IsReadScriptAvailable() ? GetCacheEntryInternalAsync<T>(cacheKey, token, nameof(GetOrAddResultAsync)) : GetEntryWithExpiryAsync<T>(cacheKey, token);

    private async ValueTask<ICacheEntry<T?>> GetEntryWithExpiryAsync<T>(CacheKey cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var redisKey = ToRedisKey(cacheKey, token);
        if (!IsConnected)
        {
            return DefaultEntry<T>();
        }

        var operation = StartOperation<T>(nameof(GetOrAddResultAsync));
        ICacheEntry<T?> ret = DefaultEntry<T>();
        try
        {
            var read = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.StringGetWithExpiryAsync(s.Key, s.Self.ReadFlagsFor(s.Key)).AsValueTask();
            },
            (Self: this, Key: redisKey),
            default,
            token).ConfigureAwait(false);
            _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), read.Value);

            var (found, deserialized) = InterpretReadResult<T>(read.Value);
            if (found)
            {
                ret = _cacheEntryFactory.Create<T?>(deserialized, read.Expiry is { } left ? Clock.GetUtcNow() + left : DateTimeOffset.MaxValue);
            }

            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            TrackRead(operation, ret.Found, redisKey);
        }

        return ret;
    }
}
