using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache
{
    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<T?>>>(cacheKey, generator, static (g, ct) => g(ct), PolicyDuration(policy), policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<T?>>>(cacheKey, generator, static (g, ct) => g(ct), CallerDuration(expiration), policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddCoreAsync<T, Func<CancellationToken, Task<T?>>>(cacheKey, generator, static (g, ct) => g(ct), CallerDuration(expiration), policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, state, generator, PolicyDuration(policy), policy, token);

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, state, generator, CallerDuration(expiration), policy, token);

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        GetOrAddCoreAsync(cacheKey, state, generator, CallerDuration(expiration), policy, token);

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(KeyValuePair{CacheKey, TState}[], Func{TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy?, CancellationToken)"/>
    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, generator, (pairs, t) => SetAsync(pairs, policy, t), policy, token);

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(KeyValuePair{CacheKey, TState}[], Func{TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy?, CancellationToken)"/>
    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(KeyValuePair{CacheKey, TState}[], Func{TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy?, CancellationToken)"/>
    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, argument, generator, (pairs, t) => SetAsync(pairs, policy, t), policy, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, argument, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull =>
        GetOrAddBatchAsync(entries, argument, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    public ValueTask<T?> GetOrAddWithExpirationAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var format = FormatFor(policy, forWrite: true, alsoReads: true);
        return format.IsDefault
            ? GetOrAddGeneratedAsync(cacheKey, ToRedisKey(cacheKey, token), generator, static (g, ct) => g(ct), policy, default(PlainFormat), token)
            : GetOrAddGeneratedAsync(cacheKey, ToRedisKey(cacheKey, token), generator, static (g, ct) => g(ct), policy, format, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var format = FormatFor(policy, forWrite: true, alsoReads: true);
        return format.IsDefault
            ? GetOrAddGeneratedAsync(cacheKey, ToRedisKey(cacheKey, token), state, generator, policy, default(PlainFormat), token)
            : GetOrAddGeneratedAsync(cacheKey, ToRedisKey(cacheKey, token), state, generator, policy, format, token);
    }

    private ValueTask<KeyValuePair<TState, T?>[]> GetOrAddBatchAsync<T, TState>(
        KeyValuePair<CacheKey, TState>[] entries,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        Func<KeyValuePair<CacheKey, T?>[], CancellationToken, ValueTask<bool>> setAsync,
        CachePolicy? policy,
        CancellationToken token)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddBatchAsync(entries, generator, DirectBatch<T, TState>.Invoke, setAsync, policy, token);
    }

    private ValueTask<KeyValuePair<TState, T?>[]> GetOrAddBatchAsync<T, TState, TArg>(
        KeyValuePair<CacheKey, TState>[] entries,
        TArg argument,
        Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        Func<KeyValuePair<CacheKey, T?>[], CancellationToken, ValueTask<bool>> setAsync,
        CachePolicy? policy,
        CancellationToken token)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(generator);
        var wrappedGenerator = WrapBatchWithFactoryTimeout(entries, generator, (policy ?? DefaultPolicy)?.FactoryTimeout);
        return BatchGetOrAdd.RunAsync(this, entries, argument, wrappedGenerator, setAsync, policy, token);
    }

    private Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> WrapBatchWithFactoryTimeout<T, TState, TArg>(
        KeyValuePair<CacheKey, TState>[] entries,
        Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        TimeSpan? factoryTimeout)
        where TState : notnull
    {
        if (factoryTimeout is null || factoryTimeout.Value <= TimeSpan.Zero || entries is not { Length: > 0 })
        {
            return generator;
        }
        return (argument, states, token) => FactoryTimeout.RunAsync(
            t => generator(argument, states, t),
            factoryTimeout,
            CompositeCacheKey.For(Array.ConvertAll(entries, e => e.Key)),
            Name,
            Telemetry,
            token);
    }

    private ValueTask<T?> GetOrAddCoreAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(generator);
        var redisKey = ToRedisKey(cacheKey, token);
        var format = FormatFor(policy, forWrite: true, alsoReads: true);
        return format.IsDefault
            ? GetOrAddInternalAsync(cacheKey, redisKey, state, generator, (policy ?? DefaultPolicy)?.FactoryTimeout, duration, default(PlainFormat), token)
            : GetOrAddInternalAsync(cacheKey, redisKey, state, generator, (policy ?? DefaultPolicy)?.FactoryTimeout, duration, format, token);
    }

    private ValueTask<T?> GetOrAddInternalAsync<T, TState, TFormat>(CacheKey cacheKey, RedisKey redisKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan? factoryTimeout, TimeSpan expiration, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ShareGenerationAsync<T, (RedisCache Cache, CacheKey Key, RedisKey RedisKey, TState State, Func<TState, CancellationToken, Task<T?>> Generator, TimeSpan? FactoryTimeout, TimeSpan Expiration, TFormat Format)>(
            redisKey,
            (this, cacheKey, redisKey, state, generator, factoryTimeout, expiration, format),
            static (args, run) => args.Cache.GenerateAndStoreAsync(args.Key, args.RedisKey, args.State, args.Generator, args.FactoryTimeout, args.Expiration, args.Format, run),
            token);
    }

    /// <summary>The work of a shared run, which ends the run itself (see <see cref="IInFlightEnd{TResult}"/>): out of the table with its result or failure before its task completes.</summary>
    private async ValueTask<T?> GenerateAndStoreAsync<T, TState, TFormat>(CacheKey cacheKey, RedisKey redisKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan? factoryTimeout, TimeSpan expiration, TFormat format, IInFlightEnd<T?> run)
        where TFormat : struct, IValueFormat
    {
        T? ret;
        try
        {
            var token = run.Token;
            // The one read of the run: every caller of the key shares it, a hit or a miss. Inlined rather than awaited
            // through ReadGetOrAddProbeAsync, so a hit runs one async frame fewer; a change to one goes in both.
            token.ThrowIfCancellationRequested();
            var found = false;
            T? cached = default;
            if (IsConnected)
            {
                var operation = StartOperation<T>(nameof(GetOrAddAsync));
                try
                {
                    var value = await ReadStringAsync(redisKey, token).ConfigureAwait(false);
                    (found, cached, var unreadable) = InterpretReadResult<T, TFormat>(redisKey, value, format);
                    if (unreadable != 0)
                    {
                        await RemoveUnreadableAsync<T>(redisKey, value, unreadable, token).ConfigureAwait(false);
                    }

                    operation.Stop();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    operation.Cancel();
                    throw;
                }
                catch (Exception ex) when (format.Handles(ex))
                {
                    operation.Stop();
                    LogRedisCacheException(ex);
                }
                finally
                {
                    TrackRead(operation, found, redisKey);
                }
            }

            if (found)
            {
                // The probe reads no TTL; a result call that joined this run reads it itself.
                run.ExpirationUnread = true;
                ret = cached;
            }
            else
            {
                LogCacheMissed(Logged(cacheKey, redisKey, typeof(T)));
                ret = await RunGeneratorAsync(cacheKey, state, generator, factoryTimeout, token).ConfigureAwait(false);

                if ((!IsDefault(ret) || _cacheNullValues) && await SetInternalAsync(cacheKey, redisKey, ret, expiration, format, token).ConfigureAwait(false))
                {
                    run.Expiration = StoredDeadline(expiration);
                }
            }
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            throw;
        }

        run.Complete(ret);
        return ret;
    }

    /// <summary>One run per Redis key and type: concurrent callers join the run in progress, which reads Redis once and generates only on a miss, and share its result or failure; it runs on its own token, cancelled once every caller has cancelled. Keyed by the Redis key, not the cache key, which a strategy may map to different Redis keys, and by value, so a prefixed key is never rendered to a string. The work ends the run itself, so the caller that started it awaits the work's own task.</summary>
    private ValueTask<T?> ShareGenerationAsync<T, TArgs>(RedisKey redisKey, TArgs args, Func<TArgs, IInFlightEnd<T?>, ValueTask<T?>> work, CancellationToken token) =>
        GenerationsOf<T>().StartOrJoinAsync(new HashedRedisKey(redisKey), (Args: args, Work: work), static (state, run) => state.Work(state.Args, run), token, out _);

    /// <summary>As <see cref="ShareGenerationAsync{T, TArgs}(RedisKey, TArgs, Func{TArgs, IInFlightEnd{T}, ValueTask{T}}, CancellationToken)"/>, also handing back the run, whose <see cref="IInFlightRun.Expiration"/> is what the starter stored.</summary>
    private ValueTask<T?> ShareGenerationAsync<T, TArgs>(RedisKey redisKey, TArgs args, Func<TArgs, IInFlightEnd<T?>, ValueTask<T?>> work, CancellationToken token, out IInFlightRun flight) =>
        GenerationsOf<T>().StartOrJoinAsync(new HashedRedisKey(redisKey), (Args: args, Work: work), static (state, run) => state.Work(state.Args, run), token, out _, out flight);

    /// <summary>The runs of one value type, so a result needs no box and no cast.</summary>
    private InFlight<HashedRedisKey, T?> GenerationsOf<T>() =>
        (InFlight<HashedRedisKey, T?>)_generations.GetOrAdd(typeof(T), static _ => new InFlight<HashedRedisKey, T?>());

    private ValueTask<T?> GetOrAddGeneratedAsync<T, TState, TFormat>(CacheKey cacheKey, RedisKey redisKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat =>
        ShareGenerationAsync<T, (RedisCache Cache, CacheKey Key, RedisKey RedisKey, TState State, Func<TState, CancellationToken, Task<GeneratedValue<T>>> Generator, CachePolicy? Policy, TFormat Format)>(
            redisKey,
            (this, cacheKey, redisKey, state, generator, policy, format),
            static (args, run) => args.Cache.GenerateExpiringAndStoreAsync(args.Key, args.RedisKey, args.State, args.Generator, args.Policy, args.Format, run),
            token);

    /// <summary>As <see cref="GenerateAndStoreAsync{T, TState, TFormat}"/>, for a generator that returns the value's deadline; ends the run itself.</summary>
    private async ValueTask<T?> GenerateExpiringAndStoreAsync<T, TState, TFormat>(CacheKey cacheKey, RedisKey redisKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, TFormat format, IInFlightEnd<T?> run)
        where TFormat : struct, IValueFormat
    {
        T? ret;
        try
        {
            var token = run.Token;
            var (found, cached) = await ReadGetOrAddProbeAsync<T, TFormat>(redisKey, format, token).ConfigureAwait(false);
            if (found)
            {
                // The probe reads no TTL; a result call that joined this run reads it itself.
                run.ExpirationUnread = true;
                ret = cached;
            }
            else
            {
                LogCacheMissed(Logged(cacheKey, redisKey, typeof(T)));
                var generated = await RunGeneratorAsync(cacheKey, state, generator, (policy ?? DefaultPolicy)?.FactoryTimeout, token).ConfigureAwait(false);

                var duration = generated.Expiration is { } expiration ? Remaining(expiration) : PolicyDuration(policy);
                if (duration is { } lifetime && (!IsDefault(generated.Value) || _cacheNullValues)
                    && await SetInternalAsync(cacheKey, redisKey, generated.Value, lifetime, format, token).ConfigureAwait(false))
                {
                    run.Expiration = StoredDeadline(lifetime);
                }

                ret = generated.Value;
            }
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            throw;
        }

        run.Complete(ret);
        return ret;
    }

    private TimeSpan? Remaining(DateTimeOffset expiration)
    {
        if (expiration == DateTimeOffset.MaxValue)
        {
            return TimeSpan.MaxValue;
        }

        var remaining = expiration - Clock.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    private Task<TResult> RunGeneratorAsync<TResult, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<TResult>> generator, TimeSpan? factoryTimeout, CancellationToken token) =>
        factoryTimeout is null || factoryTimeout.Value <= TimeSpan.Zero
            ? generator(state, token)
            : FactoryTimeout.RunAsync(ct => generator(state, ct), factoryTimeout, cacheKey, Name, Telemetry, token);
}
