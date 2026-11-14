using UiPath.Caching.Serialization;

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

    private async ValueTask<CacheResult<T>> AttributeAsync<T>(ValueTask<T?> shared, IInFlightRun run, GeneratorRecorder<T> recorder, CacheKey cacheKey, CancellationToken token)
    {
        var value = await shared.ConfigureAwait(false);
        var expiration = run.ExpirationUnread ? await ReadDeadlineAsync<T>(cacheKey, token).ConfigureAwait(false) : run.Expiration;
        return new CacheResult<T>(value, expiration, recorder.Ran);
    }

    /// <summary>The deadline of a hit that a plain call's read found without its TTL: one PTTL, only for a result call that joined that read. A separate command after the shared GET: if the key is rewritten in between, the deadline is the new value's; if it is deleted, null.</summary>
    private async ValueTask<DateTimeOffset?> ReadDeadlineAsync<T>(CacheKey cacheKey, CancellationToken token)
    {
        var left = await KeyTimeToLiveAsync(_read, StartOperation<T>(nameof(GetOrAddResultAsync)), _redisKeyStrategy, cacheKey, _logFailure, token).ConfigureAwait(false);
        return left is { } lifetime ? StoredDeadline(lifetime) : null;
    }

    private ValueTask<CacheResult<T>> GetOrAddResultCoreAsync<T>(CacheKey cacheKey, GeneratorRecorder<T> recorder, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: true);
        return format.IsDefault
            ? GetOrAddResultCoreAsync(cacheKey, recorder, duration, policy, default(PlainFormat), token)
            : GetOrAddResultCoreAsync(cacheKey, recorder, duration, policy, format, token);
    }

    private ValueTask<CacheResult<T>> GetOrAddResultCoreAsync<T, TFormat>(CacheKey cacheKey, GeneratorRecorder<T> recorder, TimeSpan duration, CachePolicy? policy, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var redisKey = ToRedisKey(cacheKey, token);
        var shared = ShareGenerationAsync<T, (RedisCache Cache, CacheKey Key, RedisKey RedisKey, GeneratorRecorder<T> Recorder, TimeSpan? FactoryTimeout, TimeSpan Duration, TFormat Format)>(
            redisKey,
            (this, cacheKey, redisKey, recorder, (policy ?? DefaultPolicy)?.FactoryTimeout, duration, format),
            static (args, run) => args.Cache.ReadOrGenerateResultAsync(args.Key, args.RedisKey, args.Recorder, args.FactoryTimeout, args.Duration, args.Format, run),
            token,
            out var flight);
        return AttributeAsync(shared, flight, recorder, cacheKey, token);
    }

    /// <summary>The lifetime a write kept, as the deadline to report; none for an entry without expiry.</summary>
    private DateTimeOffset? StoredDeadline(TimeSpan lifetime)
    {
        var deadline = Clock.ToDateTimeOffset(lifetime);
        return deadline == DateTimeOffset.MaxValue ? null : deadline;
    }

    /// <summary>The work of the result run, which ends the run itself (see <see cref="IInFlightEnd{TResult}"/>) once the expiration to report is recorded on it.</summary>
    private async ValueTask<T?> ReadOrGenerateResultAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, GeneratorRecorder<T> recorder, TimeSpan? factoryTimeout, TimeSpan duration, TFormat format, IInFlightEnd<T?> run)
        where TFormat : struct, IValueFormat
    {
        T? ret;
        try
        {
            var token = run.Token;

            // One read of value and expiration together, shared by every caller of the key.
            var entry = await GetCacheEntryInternalAsync<T, TFormat>(cacheKey, format, token, nameof(GetOrAddResultAsync)).ConfigureAwait(false);
            if (entry.Found)
            {
                run.Expiration = entry.Expiration == DateTimeOffset.MaxValue ? null : entry.Expiration;
                ret = entry.Value;
            }
            else
            {
                LogCacheMissed(Logged(cacheKey, redisKey, typeof(T)));
                ret = await RunGeneratorAsync(cacheKey, recorder.Generator, static (g, ct) => g(ct), factoryTimeout, token).ConfigureAwait(false);
                // Only a value that was written has an expiration to report.
                if ((!IsDefault(ret) || _cacheNullValues) && await SetInternalAsync(cacheKey, redisKey, ret, duration, format, token).ConfigureAwait(false))
                {
                    run.Expiration = StoredDeadline(duration);
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
}
