using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed partial class MultilayerCache : MultilayerCacheBase, ICache, ISpanKeyCache
{
    private readonly ICache _innerCache;
    private readonly InFlight<InFlightKey, object?> _generations = new();
    private readonly Lock _batchReserveGate = new();
    private readonly CacheEntryBuilder _entryBuilder;
    private readonly LocalMemorySetter _localMemorySetter;

    public MultilayerCache(
        string cacheName,
        ICache innerCache,
        IMemoryCacheFactory memoryCacheFactory,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IMultilayerCacheOptions multiLayerCacheOptions,
        IMemoryCacheOptions memoryCacheOptions,
        CacheOptions cacheOptions,
        IDistributedLock distributedLock,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        ILogger logger,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
        : base(cacheName, innerCache, memoryCacheFactory, topicFactory, cacheEventFactory, telemetryProvider, multiLayerCacheOptions, memoryCacheOptions, cacheOptions, distributedLock, policyFactory, clock, logger, keyMaskingPolicy)
    {
        _innerCache = innerCache;
        var cacheKeyStrategy = _multiLayerCacheOptions.CacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        var topicKeyStrategy = _multiLayerCacheOptions.TopicKeyStrategy ?? new DefaultTopicKeyStrategy(cacheOptions.Separator);
        _entryBuilder = new CacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _localMemorySetter = new LocalMemorySetter(cacheName, changeTokenFactory, _topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions, memoryCacheOptions, telemetryProvider, _masker, !_clearLocalOnReconnect)
        {
            Invalidations = innerCache as IInvalidationSink,
        };
    }

    /// <summary>Runs and inner reads still in the tables; tests check they drain once callers are done.</summary>
    /// <summary>The tier told of received invalidations: the inner cache when it tracks them; tests check the wiring.</summary>
    internal IInvalidationSink? Invalidations => _localMemorySetter.Invalidations;

    internal int RunsInFlight => _innerReads.Count + _generations.Count + _background.InFlight;

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, token, out var local))
        {
            return new ValueTask<T?>(local.Value);
        }

        policy ??= _defaultPolicy;
        return GetInnerAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token), policy);
    }

    public ValueTask<T?> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (TryGetLocal<T>(cacheKey, token, out var local))
        {
            return new ValueTask<T?>(local.Value);
        }

        return GetAsync<T>(new CacheKey(cacheKey), policy, token);
    }

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var options = Array.ConvertAll(cacheKeys, k => _entryBuilder.BuildEntryOptions<T>(k, default, token));
        return GetInnerAsync<T>(options, policy, token);
    }

    [OverloadResolutionPriority(1)]
    public async ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T?>(cacheKey, token, out var local))
        {
            return local;
        }

        policy ??= _defaultPolicy;
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token);
        return await GetCacheEntryInnerAsync<T>(options, policy).ConfigureAwait(false);
    }

    public ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T?>(cacheKey, token, out var local))
        {
            return new ValueTask<ICacheEntry<T?>>(local);
        }

        return GetCacheEntryAsync<T>(new CacheKey(cacheKey), policy, token);
    }

    public async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var options = Array.ConvertAll(cacheKeys, k => _entryBuilder.BuildEntryOptions<T>(k, default, token));
        return await GetCacheEntriesInnerAsync<T>(options, policy, token).ConfigureAwait(false);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        // One floored resolution for the write and the rehydrate threshold; the miss path draws the jittered write deadline, so a hit never reads the clock.
        return GetOrAddInternalAsync(cacheKey, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddResolvedAsync(new CacheKey(cacheKey), generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, state, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddResolvedAsync(new CacheKey(cacheKey), state, generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        // One floored resolution for the write and the rehydrate threshold; only the write is jittered.
        var duration = ResolveDuration(policy);
        var writeExpiration = _clock.ToDateTimeOffset(ApplyJitter(duration, policy.JitterMaxDuration));
        return GetOrAddBatchInternalAsync<T, TState, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>>>(entries, generator, DirectBatch<T, TState>.Invoke, writeExpiration, duration, policy.JitterMaxDuration, policy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddBatchInternalAsync<T, TState, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>>>(entries, generator, DirectBatch<T, TState>.Invoke, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddBatchInternalAsync<T, TState, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>>>(entries, generator, DirectBatch<T, TState>.Invoke, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        // One floored resolution for the write and the rehydrate threshold; only the write is jittered.
        var duration = ResolveDuration(policy);
        var writeExpiration = _clock.ToDateTimeOffset(ApplyJitter(duration, policy.JitterMaxDuration));
        return GetOrAddBatchInternalAsync<T, TState, TArgument>(entries, argument, generator, writeExpiration, duration, policy.JitterMaxDuration, policy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddBatchInternalAsync<T, TState, TArgument>(entries, argument, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddBatchInternalAsync<T, TState, TArgument>(entries, argument, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddGeneratedAsync(cacheKey, generator, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddGeneratedAsync(new CacheKey(cacheKey), generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddGeneratedAsync(new CacheKey(cacheKey), state, generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddGeneratedAsync(cacheKey, state, generator, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetCoreAsync(cacheKey, value, GetExpiration(policy), policy, token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), value, policy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), value, expiration, policy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), value, expiration, policy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetCoreAsync(keyValues, GetExpiration(policy), policy, token);
    }

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, GetExpiration(expiration), policy ?? _defaultPolicy, token);


    /// <summary>
    /// One path for every provider: take the local lock, probe the local tier, let the L2 decide,
    /// then populate the local tier — the reverse of <c>SetAsync</c>, which writes both tiers
    /// unconditionally. The probe is what narrows an L2 that retains nothing, and so grants every
    /// caller a win, back to one winner per process; where the L2 does arbitrate, a local hit means
    /// the key was already claimed or read here, so the loss is reported without a round-trip. That
    /// can cost a win the L2 would have granted, when the local copy outlived the shared one — the
    /// fail-closed direction the ambiguous <c>false</c> already covers. A disconnected L2 answers
    /// for itself (<c>RedisCache</c> checks its connection first) rather than being gated on
    /// <c>GetInnerCacheDisconnected</c>, whose state also covers the broadcast transport: a dead
    /// topic must not stop a healthy Redis.
    /// </summary>
    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return TryAddCoreAsync(cacheKey, value, GetExpiration(policy), policy, token);
    }

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token));
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = Array.ConvertAll(cacheKey, k => _entryBuilder.BuildEntryOptions<T>(k, default, token));
        return RemoveAsync<T>(options, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return RefreshCoreAsync<T>(cacheKey, GetExpiration(policy), policy, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    [OverloadResolutionPriority(1)]
    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, token, out _))
        {
            return true;
        }

        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);
        try
        {
            return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey.Name, out _) || await _innerCache.ContainsAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheContainsError(ex, Logged(cacheKey, typeof(T)));
            return false;
        }
    }

    public ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, token, out _)
            ? new ValueTask<bool>(true)
            : ContainsAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var value)
            ? value?.Expiration.Subtract(_clock.GetUtcNow())
            : await _innerCache.TimeToLiveAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, token, out var local)
            ? new ValueTask<TimeSpan?>(local.Expiration.Subtract(_clock.GetUtcNow()))
            : TimeToLiveAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);

        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, token, out var local)
            ? new ValueTask<DateTimeOffset?>(local.Expiration)
            : ExpireTimeAsync<T>(new CacheKey(cacheKey), token);
    }

    private static async ValueTask<ICacheEntry<T?>> EntryOfAsync<T>(ValueTask<object?> pending) =>
        (ICacheEntry<T?>)(await pending.ConfigureAwait(false))!;

    private static async ValueTask<T?> ValueOfAsync<T>(ValueTask<ICacheEntry<T?>> entry) =>
        (await entry.ConfigureAwait(false)).Value;

    /// <summary>Reports a failure through the task, as an async method does, for the methods that are not async.</summary>
    private static async ValueTask<T?> FailedAsync<T>(Exception failure)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        ExceptionDispatchInfo.Throw(failure);
        return default;
    }

    // A closure-form batch passes its generator as the argument with DirectBatch.Invoke, and gets it back unwrapped.
    private static Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> BindBatch<T, TState, TArgument>(TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator)
        where TState : notnull =>
        argument is Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> direct && ReferenceEquals(generator, DirectBatch<T, TState>.Invoke)
            ? direct
            : StatefulCacheExtensions.BindBatch(argument, generator);

    private static Func<CancellationToken, Task<T?>> Bind<T, TState>(TState state, Func<TState, CancellationToken, Task<T?>> generator) =>
        token => generator(state, token);

    /// <summary>Stops every join counting as a waiter, so a shared run no caller still wants is cancelled, and observes the failure of one nobody awaits.</summary>
    private static void WithdrawAll<T>(List<BatchJoin<T>> joined)
    {
        foreach (var join in joined)
        {
            join.Join.Withdraw();
            join.Run.Forget();
        }
    }

    /// <summary>Waits for the runs this call joined; the indexes whose run was given up come back for another try.</summary>
    private static async ValueTask<List<int>> AwaitJoinedAsync<T>(List<BatchJoin<T>> joined, CacheEntryOptions[] missOptions, KeyValuePair<CacheKey, ICacheEntry<T?>>[] result)
    {
        var abandoned = new List<int>();
        try
        {
            foreach (var (index, _, run) in joined)
            {
                try
                {
                    result[index] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(missOptions[index].CacheKey, await run.ConfigureAwait(false));
                }
                catch (InFlightAbandonedException)
                {
                    abandoned.Add(index);
                }
            }
        }
        catch (Exception)
        {
            WithdrawAll(joined);
            throw;
        }

        return abandoned;
    }

    private static void EndOwned(List<BatchRun> owned, Exception failure)
    {
        foreach (var run in owned)
        {
            run.Run.Fail(failure);
        }
    }

    private static async Task<ICacheEntry<T?>> AsEntryAsync<T>(Task<object?> run) => (ICacheEntry<T?>)(await run.ConfigureAwait(false))!;

    /// <summary>Translates the reserved caller keys back into the generator's states.</summary>
    private static TState[] MapReservedKeysToStates<TState>(CacheKey[] reservedKeys, Dictionary<CacheKey, TState> stateByCallerKey)
        where TState : notnull
    {
        var rehydrateStates = new TState[reservedKeys.Length];
        for (var i = 0; i < reservedKeys.Length; i++)
        {
            rehydrateStates[i] = stateByCallerKey[reservedKeys[i]];
        }
        return rehydrateStates;
    }

    /// <summary>Restricts the generator's output to what we asked for; the first value wins per state.</summary>
    private static Dictionary<TState, T?> SelectRequestedProduced<T, TState>(
        KeyValuePair<TState, T?>[]? produced,
        TState[] requestStates)
        where TState : notnull
    {
        var producedByState = new Dictionary<TState, T?>(requestStates.Length);
        var requested = new HashSet<TState>(requestStates);
        foreach (var pair in (produced ?? []).Where(pair => requested.Contains(pair.Key)))
        {
            _ = producedByState.TryAdd(pair.Key, pair.Value);
        }
        return producedByState;
    }

    private static KeyValuePair<TState, T?>[] Project<T, TState>(TState[] states, int[] keyIndexOfState, T?[] values)
        where TState : notnull
    {
        var results = new KeyValuePair<TState, T?>[states.Length];
        for (var i = 0; i < states.Length; i++)
        {
            results[i] = new KeyValuePair<TState, T?>(states[i], values[keyIndexOfState[i]]);
        }
        return results;
    }

    [SuppressMessage("Major Code Smell", "S3267:Loops should be simplified with LINQ expressions", Justification = "A closure per call on the batch hit path.")]
    private static bool HasRepeatedState<TState>(KeyValuePair<CacheKey, TState>[] entries)
        where TState : notnull
    {
        if (entries.Length <= 16)
        {
            for (var i = 1; i < entries.Length; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    if (EqualityComparer<TState>.Default.Equals(entries[i].Value, entries[j].Value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var seen = new HashSet<TState>(entries.Length);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A local hit only, never the inner tier: for a caller that has just taken a key, where the only thing that can have changed is a run of this process.</summary>
    private bool TryGetLocalEntry<T>(CacheEntryOptions options, [MaybeNullWhen(false)] out ICacheEntry<T?> entry)
    {
        if ((_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) && _memoryCache.TryGetValue(options.CacheKey.Name, out entry) && entry!.Found)
        {
            return true;
        }

        entry = default;
        return false;
    }

    private ValueTask<T?> GetOrAddResolvedAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, long seen, bool localMissed, CancellationToken token)
    {
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, seen, localMissed, token);
    }

    private ValueTask<T?> GetOrAddResolvedAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, long seen, bool localMissed, CancellationToken token)
    {
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, state, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, seen, localMissed, token);
    }

    /// <param name="expiration">The caller's write deadline, or null for a policy write, whose jittered deadline the miss path draws.</param>
    /// <param name="seen">The local writes counter, read before the caller's first read of the local tier.</param>
    /// <param name="localMissed">True when that read found nothing under the key, so the miss path need not read it again.</param>
    private ValueTask<T?> GetOrAddInternalAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // Not async: the miss path's lock delegates capture these parameters, and an async method would build that closure on a hit too.
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !token.IsCancellationRequested)
        {
            var probe = ProbeLocal<T?>(cacheKey, token, out var local);
            if (probe == LocalProbe.Hit && local!.Found)
            {
                Layered<T>().TryRehydrate(cacheKey, local.Expiration, local.Value, generator, policy, effectiveDuration, rehydrateJitter);
                return new ValueTask<T?>(local.Value);
            }

            localMissed = probe == LocalProbe.Miss;
        }

        return GetOrAddCoreAsync(cacheKey, generator, expiration, effectiveDuration, rehydrateJitter, policy, seen, localMissed, token);
    }

    private ValueTask<T?> GetOrAddInternalAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // Binds the state to a delegate only where one is needed, so a hit builds no closure.
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !token.IsCancellationRequested)
        {
            var probe = ProbeLocal<T?>(cacheKey, token, out var local);
            if (probe == LocalProbe.Hit && local!.Found)
            {
                var layered = Layered<T>();
                if (layered.ShouldRehydrate(local.Value, policy, effectiveDuration))
                {
                    layered.TriggerRehydrate(cacheKey, local.Expiration, Bind(state, generator), policy, effectiveDuration, rehydrateJitter);
                }
                return new ValueTask<T?>(local.Value);
            }

            localMissed = probe == LocalProbe.Miss;
        }

        return GetOrAddCoreAsync(cacheKey, Bind(state, generator), expiration, effectiveDuration, rehydrateJitter, policy, seen, localMissed, token);
    }

    private ValueTask<T?> GetOrAddCoreAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // Not async: a miss that never waits builds no state machine, and one that waits builds one, where an async method here would build one for the wait at every step.
        try
        {
            var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration ?? _clock.ToDateTimeOffset(ApplyJitter(effectiveDuration, rehydrateJitter)), token);
            var fetch = localMissed ? Layered<T>().FetchInnerAsync(options, policy) : GetCacheEntryInnerAsync<T>(options, policy);
            var miss = new Miss<T>(cacheKey, options, generator, effectiveDuration, rehydrateJitter, policy, seen);
            if (fetch.IsCompletedSuccessfully)
            {
                return GenerateOnMissAsync(miss, fetch.Result, token);
            }

            return AwaitFetchAsync(miss, fetch, token);
        }
        catch (Exception ex)
        {
            return FailedAsync<T>(ex);
        }
    }

    private ValueTask<T?> GenerateOnMissAsync<T>(Miss<T> miss, ICacheEntry<T?> entry, CancellationToken token)
    {
        if (entry.Found)
        {
            Layered<T>().TryRehydrate(miss.Key, entry.Expiration, entry.Value, miss.Generator, miss.Policy, miss.Duration, miss.RehydrateJitter);
            return new ValueTask<T?>(entry.Value);
        }

        var generated = GetOrGenerateEntryAsync<T, Func<CancellationToken, Task<T?>>>(miss.Options, miss.Generator, miss.Policy, static (cache, options, g, p, scope) => cache.RunGeneratorAndStoreEntryAsync(options, g, p, scope), miss.Seen, token);
        if (generated.IsCompletedSuccessfully)
        {
            return new ValueTask<T?>(generated.Result.Value);
        }

        return ValueOfAsync(generated);
    }

    private async ValueTask<T?> AwaitFetchAsync<T>(Miss<T> miss, ValueTask<ICacheEntry<T?>> fetch, CancellationToken token)
    {
        var entry = await fetch.ConfigureAwait(false);
        return await GenerateOnMissAsync(miss, entry, token).ConfigureAwait(false);
    }


    private void TryRehydrateBatch<T, TState>(
        List<(CacheKey CallerKey, TState State, CacheEntryOptions Options, DateTimeOffset Expiration, T? Value)> hits,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        TimeSpan duration,
        TimeSpan? rehydrateJitter)
        where TState : notnull
    {
        if (policy.RehydrateEnabled != true || policy.Rehydrate is null)
        {
            return;
        }
        if (duration <= TimeSpan.Zero || duration == TimeSpan.MaxValue)
        {
            return;
        }

        var (candidates, stateByCallerKey, byState) = SelectRehydrateCandidates(hits);
        if (candidates.Count == 0)
        {
            return;
        }

        _rehydrator.TryTriggerBatch(
            candidates,
            policy,
            duration,
            kind: "cache",
            rehydrateAsync: (rehydrateKeys, ct) =>
                RehydrateReservedAsync<T, TState>(rehydrateKeys, stateByCallerKey, byState, generator, policy, duration, rehydrateJitter, ct),
            entryType: typeof(T));
    }

    /// <summary>The hits worth rehydrating, plus the two lookups the background callback needs.</summary>
    private (List<(CacheKey Key, DateTimeOffset Expiration)> Candidates, Dictionary<CacheKey, TState> StateByCallerKey, Dictionary<TState, RehydrateTarget> ByState) SelectRehydrateCandidates<T, TState>(
        List<(CacheKey CallerKey, TState State, CacheEntryOptions Options, DateTimeOffset Expiration, T? Value)> hits)
        where TState : notnull
    {
        var candidates = new List<(CacheKey Key, DateTimeOffset Expiration)>(hits.Count);
        var stateByCallerKey = new Dictionary<CacheKey, TState>(hits.Count);
        var byState = new Dictionary<TState, RehydrateTarget>(hits.Count);
        foreach (var hit in hits)
        {
            if (hit.Value is null && _multiLayerCacheOptions.CacheNullValues)
            {
                continue;
            }
            candidates.Add((hit.CallerKey, hit.Expiration));
            stateByCallerKey[hit.CallerKey] = hit.State;
            byState[hit.State] = new RehydrateTarget(hit.Options, hit.Expiration, hit.CallerKey);
        }
        return (candidates, stateByCallerKey, byState);
    }

    /// <summary>Rehydrates the subset of caller keys the coordinator reserved.</summary>
    private async ValueTask RehydrateReservedAsync<T, TState>(
        CacheKey[] reservedKeys,
        Dictionary<CacheKey, TState> stateByCallerKey,
        Dictionary<TState, RehydrateTarget> byState,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        TimeSpan duration,
        TimeSpan? rehydrateJitter,
        CancellationToken token)
        where TState : notnull
    {
        var rehydrateStates = MapReservedKeysToStates(reservedKeys, stateByCallerKey);
        var produced = await generator(rehydrateStates, token).ConfigureAwait(false);

        var groups = GroupRehydratedByExpiration<T, TState>(produced, rehydrateStates, byState, duration, rehydrateJitter);
        if (groups.Count == 0)
        {
            return;
        }

        await WriteRehydrateGroupsAsync(groups, policy, token).ConfigureAwait(false);
    }

    /// <summary>Groups the produced pairs by target expiration, which <c>InternalSetAsync</c> applies per write.</summary>
    private Dictionary<DateTimeOffset, List<(CacheEntryValue<T> Entry, CacheKey CallerKey)>> GroupRehydratedByExpiration<T, TState>(
        KeyValuePair<TState, T?>[]? produced,
        TState[] rehydrateStates,
        Dictionary<TState, RehydrateTarget> byState,
        TimeSpan duration,
        TimeSpan? rehydrateJitter)
        where TState : notnull
    {
        var requested = new HashSet<TState>(rehydrateStates);
        var seen = new HashSet<TState>(rehydrateStates.Length);

        var freshExpiration = _clock.ToDateTimeOffset(ApplyJitter(duration, rehydrateJitter));
        var groups = new Dictionary<DateTimeOffset, List<(CacheEntryValue<T> Entry, CacheKey CallerKey)>>();
        foreach (var pair in produced ?? [])
        {
            if (!requested.Contains(pair.Key) || !seen.Add(pair.Key))
            {
                continue;
            }
            if (pair.Value is null && !_multiLayerCacheOptions.CacheNullValues)
            {
                continue;
            }
            var (options, originalExpiration, callerKey) = byState[pair.Key];
            var target = pair.Value is null ? originalExpiration : freshExpiration;
            options.Expiration = target;
            if (!groups.TryGetValue(target, out var group))
            {
                group = [];
                groups[target] = group;
            }
            group.Add((new CacheEntryValue<T>(options, pair.Value), callerKey));
        }
        return groups;
    }

    /// <summary>Publishes then writes one expiration group at a time, reporting every caller key that failed.</summary>
    private async ValueTask WriteRehydrateGroupsAsync<T>(
        Dictionary<DateTimeOffset, List<(CacheEntryValue<T> Entry, CacheKey CallerKey)>> groups,
        CachePolicy policy,
        CancellationToken token)
    {
        var failed = new List<string>();
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        foreach (var group in groups.Values)
        {
            var fired = innerCacheDisconnected || await PublishCacheSetEventsAsync(group).ConfigureAwait(false);
            var written = fired && await InternalSetAsync<T>(group.Select(e => e.Entry).ToArray(), innerCacheDisconnected, policy, keepRefused: false, token).ConfigureAwait(false);
            if (!written)
            {
                failed.AddRange(group.Select(e => e.CallerKey.Name));
            }
        }

        if (failed.Count > 0)
        {
            throw new RehydrateWriteFailedException(string.Join(",", failed));
        }
    }

    /// <summary>Publishes a <c>CacheSet</c> event per entry, stopping at the first one that does not fire.</summary>
    private async ValueTask<bool> PublishCacheSetEventsAsync<T>(List<(CacheEntryValue<T> Entry, CacheKey CallerKey)> group)
    {
        foreach (var (entry, _) in group)
        {
            if (!await _eventPublisher.CacheSetAsync(entry.CacheEntry, typeof(T)).ConfigureAwait(false))
            {
                return false;
            }
        }
        return true;
    }

    private ValueTask<ICacheEntry<T?>> GetOrGenerateEntryAsync<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        long seen,
        CancellationToken token)
    {
        return SharesRuns(policy.Lock)
            ? RunFirstAsync<T, TGenerator>(options, generator, policy, generate, seen, token)
            : GenerateWithoutRunAsync<T, TGenerator>(options, generator, policy, generate, token);
    }

    /// <summary>With the shared run off every caller generates for itself, under the distributed lock when one is enabled.</summary>
    private ValueTask<ICacheEntry<T?>> GenerateWithoutRunAsync<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        CancellationToken token)
    {
        var pending = GenerateGuardedAsync<T, TGenerator>(options, generator, policy, generate, new GenerationScope(null, token));
        if (pending.IsCompletedSuccessfully)
        {
            return new ValueTask<ICacheEntry<T?>>((ICacheEntry<T?>)pending.Result!);
        }

        return EntryOfAsync<T>(pending);
    }

    /// <summary>The generation itself: under the key's distributed lock when one is enabled, which is taken by the caller that runs it, held until the run ends, and followed by a read of both tiers that may find what another process stored; otherwise the generator at once.</summary>
    private ValueTask<object?> GenerateGuardedAsync<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        GenerationScope scope) =>
        DistributedLockOn(policy.Lock)
            ? GenerateUnderDistributedLockAsync<T, TGenerator>(options, generator, policy, generate, scope)
            : generate(this, options, generator, policy, scope);

    private async ValueTask<object?> GenerateUnderDistributedLockAsync<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        GenerationScope scope)
    {
        await using var lease = await AcquireDistributedLockAsync(options.CacheKey, policy.Lock, scope.Token).ConfigureAwait(false);
        // Read on the run's token: the starter may have left while callers that joined still wait for this.
        var current = await GetCacheEntryInnerAsync<T>(options with { Token = scope.Token }, policy).ConfigureAwait(false);
        return current.Found ? current : await generate(this, options, generator, policy, scope).ConfigureAwait(false);
    }

    /// <summary>Takes the key's run before anything else, and runs the generator inline in the caller that took it: a caller that arrives meanwhile joins the run at once, with no lock of its own to wait for or time out of; only the caller that runs it takes the distributed lock, if one is enabled.</summary>
    private ValueTask<ICacheEntry<T?>> RunFirstAsync<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        long seen,
        CancellationToken token)
    {
        var pending = StartRun<T, TGenerator>(options, generator, policy, generate, seen, token);
        if (pending.IsCompletedSuccessfully)
        {
            return new ValueTask<ICacheEntry<T?>>((ICacheEntry<T?>)pending.Result!);
        }

        return AwaitRunAsync<T, TGenerator>(pending, options, generator, policy, generate, seen, token);
    }

    private async ValueTask<ICacheEntry<T?>> AwaitRunAsync<T, TGenerator>(
        ValueTask<object?> pending,
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        long seen,
        CancellationToken token)
    {
        while (true)
        {
            try
            {
                return (ICacheEntry<T?>)(await pending.ConfigureAwait(false))!;
            }
            catch (InFlightAbandonedException)
            {
                // The batch or TryAdd that held the key gave it up: it is up for taking again.
                pending = StartRun<T, TGenerator>(options, generator, policy, generate, seen, token);
            }
        }
    }

    private ValueTask<object?> StartRun<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        long seen,
        CancellationToken token) =>
        _generations.RunAsync(
            new InFlightKey(options.CacheKey.Name, typeof(T), null, null),
            (Cache: this, Options: options, Generator: generator, Policy: policy, Generate: generate, Seen: seen),
            static (state, run) =>
                // A write in this process since the caller read the key may have left its value in the local tier, which a run writes before it leaves the table.
                state.Cache._localMemorySetter.Writes != state.Seen && state.Cache.TryGetLocalEntry<T>(state.Options, out var current)
                    ? new ValueTask<object?>(current)
                    : state.Cache.GenerateGuardedAsync<T, TGenerator>(state.Options, state.Generator, state.Policy, state.Generate, new GenerationScope(run, run.Token)),
            token);

    private async ValueTask<object?> RunGeneratorAndStoreEntryAsync<T>(CacheEntryOptions cacheEntryOptions, Func<CancellationToken, Task<T?>> generator, CachePolicy policy, GenerationScope scope)
    {
        LogCacheMissed(Logged(cacheEntryOptions, typeof(T)));
        var ret = await InvokeFactoryAsync(cacheEntryOptions.CacheKey, generator, policy.FactoryTimeout, scope.Token).ConfigureAwait(false);
        return await StoreGeneratedEntryAsync(cacheEntryOptions, ret, policy, scope).ConfigureAwait(false);
    }

    private async ValueTask<ICacheEntry<T?>> StoreGeneratedEntryAsync<T>(CacheEntryOptions cacheEntryOptions, T? ret, CachePolicy policy, GenerationScope scope)
    {
        // One entry for the local tier and the caller: both see the same value and deadline.
        var entry = _cacheEntryFactory.Create<T?>(ret, cacheEntryOptions.Expiration);

        // A generator that ignores its token can finish after every caller left and a newer run took the key: its value must not replace theirs.
        if ((ret is not null || _multiLayerCacheOptions.CacheNullValues) && !scope.IsAbandoned)
        {
            var innerCacheDisconnected = GetInnerCacheDisconnected();
            // The starter's token, unless it cancelled while callers that joined still wait for the value: then the run's, which outlives it.
            var store = cacheEntryOptions.Token.IsCancellationRequested && !scope.IsAbandoned ? cacheEntryOptions with { Token = scope.Token } : cacheEntryOptions;
            // A peer that takes the distributed lock next must find the value.
            if (!innerCacheDisconnected && policy.BackgroundDistributedWrite == true && !DistributedLockOn(policy.Lock) && _background.TryReserve())
            {
                FillInBackground(store, ret, policy, entry);
            }
            else
            {
                await Layered<T>().InternalSetAsync(store, ret, innerCacheDisconnected, policy, keepRefused: true, entry).ConfigureAwait(false);
            }
        }
        return entry;
    }

    private async ValueTask<T?> GetOrAddGeneratedAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, GetExpiration(policy), token);

        var entry = localMissed
            ? await Layered<T>().FetchInnerAsync(cacheEntryOptions, policy).ConfigureAwait(false)
            : await GetCacheEntryInnerAsync<T>(cacheEntryOptions, policy).ConfigureAwait(false);
        if (entry.Found)
        {
            return entry.Value;
        }

        var result = await GetOrGenerateEntryAsync<T, Func<CancellationToken, Task<GeneratedValue<T>>>>(cacheEntryOptions, generator, policy, static (cache, options, g, p, scope) => cache.RunGeneratedAndStoreEntryAsync(options, g, p, scope), seen, token).ConfigureAwait(false);
        return result.Value;
    }

    private async ValueTask<T?> GetOrAddGeneratedAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // As the closure form above; the state is bound to a delegate only for the miss, so a hit builds no closure.
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, GetExpiration(policy), token);

        var entry = localMissed
            ? await Layered<T>().FetchInnerAsync(cacheEntryOptions, policy).ConfigureAwait(false)
            : await GetCacheEntryInnerAsync<T>(cacheEntryOptions, policy).ConfigureAwait(false);
        if (entry.Found)
        {
            return entry.Value;
        }

        var result = await GetOrGenerateEntryAsync<T, Func<CancellationToken, Task<GeneratedValue<T>>>>(cacheEntryOptions, StatefulCacheExtensions.BindGenerated(state, generator), policy, static (cache, options, g, p, scope) => cache.RunGeneratedAndStoreEntryAsync(options, g, p, scope), seen, token).ConfigureAwait(false);
        return result.Value;
    }

    private async ValueTask<object?> RunGeneratedAndStoreEntryAsync<T>(CacheEntryOptions cacheEntryOptions, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy policy, GenerationScope scope)
    {
        LogCacheMissed(Logged(cacheEntryOptions, typeof(T)));
        var generated = await InvokeFactoryAsync(cacheEntryOptions.CacheKey, generator, policy.FactoryTimeout, scope.Token).ConfigureAwait(false);

        if (generated.Expiration is { } expiration)
        {
            cacheEntryOptions.Expiration = expiration;
            if (expiration <= _clock.GetUtcNow())
            {
                return _cacheEntryFactory.Create<T?>(generated.Value, expiration);
            }
        }

        return await StoreGeneratedEntryAsync(cacheEntryOptions, generated.Value, policy, scope).ConfigureAwait(false);
    }

    private async ValueTask<KeyValuePair<TState, T?>[]> GetOrAddBatchInternalAsync<T, TState, TArgument>(
        KeyValuePair<CacheKey, TState>[] entries,
        TArgument argument,
        Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        DateTimeOffset? expiration,
        TimeSpan effectiveDuration,
        TimeSpan? rehydrateJitter,
        CachePolicy policy,
        CancellationToken token)
        where TState : notnull
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(entries);

        if (TryGetAllLocal<T, TState>(entries, policy, token, out var local))
        {
            return local;
        }

        var (states, keyIndexOfState, callerKeys, options, firstStateOfKey) = DistinctEntries<T, TState>(entries, expiration, token);
        if (states.Length == 0)
        {
            return [];
        }

        var seen = _localMemorySetter.Writes;
        var probe = await GetCacheEntriesInnerAsync<T>(options, policy, token).ConfigureAwait(false);

        var values = new T?[options.Length];
        var missIndices = new List<int>();
        var hits = new List<(CacheKey CallerKey, TState State, CacheEntryOptions Options, DateTimeOffset Expiration, T? Value)>();
        for (var i = 0; i < probe.Length; i++)
        {
            var entry = probe[i].Value;
            if (entry.Found)
            {
                values[i] = entry.Value;
                hits.Add((callerKeys[i], states[firstStateOfKey[i]], options[i], entry.Expiration, entry.Value));
                continue;
            }
            missIndices.Add(i);
        }

        if (hits.Count > 0 && policy.RehydrateEnabled == true && policy.Rehydrate is not null)
        {
            TryRehydrateBatch(hits, BindBatch(argument, generator), policy, effectiveDuration, rehydrateJitter);
        }

        if (missIndices.Count == 0)
        {
            return Project(states, keyIndexOfState, values);
        }

        var missOptions = new CacheEntryOptions[missIndices.Count];
        var missStates = new TState[missIndices.Count];
        var missProbe = new KeyValuePair<CacheKey, ICacheEntry<T?>>[missIndices.Count];
        for (var i = 0; i < missIndices.Count; i++)
        {
            var index = missIndices[i];
            missOptions[i] = options[index];
            missStates[i] = states[firstStateOfKey[index]];
            missProbe[i] = probe[index];
        }

        var bound = BindBatch(argument, generator);
        var resolved = SharesRuns(policy.Lock)
            ? await RunBatchSharedAsync(missOptions, missStates, missProbe, bound, policy, seen, token).ConfigureAwait(false)
            : await RunBatchGuardedAsync(missOptions, missStates, missProbe, bound, policy, token).ConfigureAwait(false);

        for (var i = 0; i < missIndices.Count; i++)
        {
            values[missIndices[i]] = resolved[i].Value.Value;
        }

        return Project(states, keyIndexOfState, values);
    }

    /// <summary>One batch generator for the keys given, under the distributed lock over them when one is enabled: the lock is taken, the keys read again in both tiers, and the generator run for those still missing.</summary>
    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> RunBatchGuardedAsync<T, TState>(
        CacheEntryOptions[] options,
        TState[] states,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        CancellationToken token)
        where TState : notnull
    {
        if (!DistributedLockOn(policy.Lock))
        {
            return await RunBatchGeneratorAndStoreAsync(options, states, probe, generator, policy, token).ConfigureAwait(false);
        }

        await using var lease = await AcquireDistributedLockAsync(CompositeCacheKey.For(Array.ConvertAll(options, o => o.CacheKey)), policy.Lock, token).ConfigureAwait(false);
        var latest = await GetCacheEntriesInnerAsync<T>(options, policy, token).ConfigureAwait(false);
        return await RunBatchGeneratorAndStoreAsync(options, states, latest, generator, policy, token).ConfigureAwait(false);
    }

    /// <summary>Joins the keys whose generator run is in flight, a single-key run or another batch, and runs one batch generator for the rest, which others in turn can join.</summary>
    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> RunBatchSharedAsync<T, TState>(
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        long seen,
        CancellationToken token)
        where TState : notnull
    {
        var result = (KeyValuePair<CacheKey, ICacheEntry<T?>>[])probe.Clone();
        var owned = new List<BatchRun>();
        var joined = new List<BatchJoin<T>>();
        try
        {
            ReserveBatchKeys(missOptions, probe, owned, joined, token);
            if (owned.Count > 0)
            {
                await RunOwnedAsync(owned, missOptions, missStates, probe, generator, policy, seen, result, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            EndOwned(owned, new InFlightAbandonedException());
            WithdrawAll(joined);
            throw;
        }
        catch (Exception ex)
        {
            EndOwned(owned, ex);
            WithdrawAll(joined);
            throw;
        }

        var abandoned = await AwaitJoinedAsync(joined, missOptions, result).ConfigureAwait(false);
        if (abandoned.Count > 0)
        {
            await RetryAbandonedAsync(abandoned, missOptions, missStates, generator, policy, result, token).ConfigureAwait(false);
        }

        return result;
    }

    private void ReserveBatchKeys<T>(CacheEntryOptions[] missOptions, KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe, List<BatchRun> owned, List<BatchJoin<T>> joined, CancellationToken token)
    {
        // Under one gate, so two batches over the same keys cannot each take some of them: the first takes every free key, the next joins them all. Nothing in it waits.
        lock (_batchReserveGate)
        {
            for (var i = 0; i < missOptions.Length; i++)
            {
                if (!probe[i].Value.Found)
                {
                    ReserveOrJoin(i, new InFlightKey(missOptions[i].CacheKey.Name, typeof(T), null, null), owned, joined, token);
                }
            }
        }
    }

    /// <summary>Runs one batch generator for the keys this call took, and ends each reservation with its entry.</summary>
    private async ValueTask RunOwnedAsync<T, TState>(
        List<BatchRun> owned,
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        long seen,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] result,
        CancellationToken token)
        where TState : notnull
    {
        // A write in this process since the caller probed may have left values in the local tier, which a run writes before it leaves the table.
        var rewritten = _localMemorySetter.Writes != seen;

        // Every key is ours, none joined, so the arrays already are the owned set and need no copy.
        var all = owned.Count == missOptions.Length;
        var ownedOptions = all ? missOptions : owned.Select(o => missOptions[o.Index]).ToArray();
        var ownedStates = all ? missStates : owned.Select(o => missStates[o.Index]).ToArray();
        var current = all && !rewritten
            ? probe
            : owned.Select(o => rewritten && TryGetLocalEntry<T>(missOptions[o.Index], out var local) ? new KeyValuePair<CacheKey, ICacheEntry<T?>>(missOptions[o.Index].CacheKey, local) : probe[o.Index]).ToArray();
        var produced = await RunBatchGuardedAsync(ownedOptions, ownedStates, current, generator, policy, token).ConfigureAwait(false);
        for (var i = 0; i < owned.Count; i++)
        {
            result[owned[i].Index] = produced[i];
            owned[i].Run.Complete(produced[i].Value);
        }
    }

    private async ValueTask RetryAbandonedAsync<T, TState>(
        List<int> abandoned,
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] result,
        CancellationToken token)
        where TState : notnull
    {
        var retryOptions = abandoned.Select(i => missOptions[i]).ToArray();
        var freshSeen = _localMemorySetter.Writes;
        var fresh = await GetCacheEntriesInnerAsync<T>(retryOptions, policy, token).ConfigureAwait(false);
        var again = await RunBatchSharedAsync(retryOptions, abandoned.Select(i => missStates[i]).ToArray(), fresh, generator, policy, freshSeen, token).ConfigureAwait(false);
        for (var i = 0; i < abandoned.Count; i++)
        {
            result[abandoned[i]] = again[i];
        }
    }

    private void ReserveOrJoin<T>(int index, InFlightKey key, List<BatchRun> owned, List<BatchJoin<T>> joined, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_generations.TryReserve(key, out var reservation))
            {
                owned.Add(new BatchRun(index, reservation));
                return;
            }

            if (_generations.TryJoin(key, token, out var shared))
            {
                joined.Add(new BatchJoin<T>(index, shared, AsEntryAsync<T>(shared.Result)));
                return;
            }

            // The run ended between the two, so the key is free again.
        }
    }

    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> RunBatchGeneratorAndStoreAsync<T, TState>(
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        CancellationToken token)
        where TState : notnull
    {
        var stillMissing = new List<int>();
        for (var i = 0; i < missOptions.Length; i++)
        {
            if (!probe[i].Value.Found)
            {
                stillMissing.Add(i);
            }
        }
        if (stillMissing.Count == 0)
        {
            return probe;
        }

        var requestStates = new TState[stillMissing.Count];
        var mappedKeys = new CacheKey[stillMissing.Count];
        // Log-only, so it is not built for a disabled level.
        var missedOptions = _logger.IsEnabled(LogLevel.Debug) ? new CacheEntryOptions[stillMissing.Count] : null;
        for (var i = 0; i < stillMissing.Count; i++)
        {
            requestStates[i] = missStates[stillMissing[i]];
            mappedKeys[i] = missOptions[stillMissing[i]].CacheKey;
            if (missedOptions is not null)
            {
                missedOptions[i] = missOptions[stillMissing[i]];
            }
        }

        var telemetryKey = CompositeCacheKey.For(mappedKeys);
        if (missedOptions is not null)
        {
            LogBatchCacheMissed(Logged(missedOptions, typeof(T)), stillMissing.Count);
        }

        var produced = await InvokeFactoryAsync(telemetryKey, ct => generator(requestStates, ct), policy.FactoryTimeout, token).ConfigureAwait(false);
        var producedByState = SelectRequestedProduced(produced, requestStates);

        var toStore = SelectEntriesToStore<T, TState>(missOptions, missStates, stillMissing, producedByState);
        if (toStore.Count > 0)
        {
            var innerCacheDisconnected = GetInnerCacheDisconnected();
            await InternalSetAsync<T>(toStore.ToArray(), innerCacheDisconnected, policy, keepRefused: true, token).ConfigureAwait(false);
        }

        return BuildBatchEntries<T, TState>(missOptions, missStates, probe, producedByState);
    }

    /// <summary>The write set: the answered still-missing slots, minus the nulls this cache does not store.</summary>
    private List<CacheEntryValue<T>> SelectEntriesToStore<T, TState>(
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        List<int> stillMissing,
        Dictionary<TState, T?> producedByState)
        where TState : notnull
    {
        var toStore = new List<CacheEntryValue<T>>(producedByState.Count);
        foreach (var index in stillMissing)
        {
            if (!producedByState.TryGetValue(missStates[index], out var value))
            {
                continue;
            }
            if (value is null && !_multiLayerCacheOptions.CacheNullValues)
            {
                continue;
            }
            toStore.Add(new CacheEntryValue<T>(missOptions[index], value));
        }
        return toStore;
    }

    /// <summary>One entry per miss-set slot: the post-lock hit if there was one, otherwise the generated value.</summary>
    private KeyValuePair<CacheKey, ICacheEntry<T?>>[] BuildBatchEntries<T, TState>(
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Dictionary<TState, T?> producedByState)
        where TState : notnull
    {
        var results = new KeyValuePair<CacheKey, ICacheEntry<T?>>[missOptions.Length];
        for (var i = 0; i < missOptions.Length; i++)
        {
            if (probe[i].Value.Found)
            {
                results[i] = probe[i];
                continue;
            }
            var wasProduced = producedByState.TryGetValue(missStates[i], out var value);
            results[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(
                missOptions[i].CacheKey,
                _cacheEntryFactory.Create<T?>(value, wasProduced ? missOptions[i].Expiration : DateTimeOffset.MinValue));
        }
        return results;
    }

    /// <summary>A batch whose keys are all local hits, answered as the full path would without building its options, lists and lookups; a miss, a rehydrating policy or a repeated state takes the full path.</summary>
    private bool TryGetAllLocal<T, TState>(KeyValuePair<CacheKey, TState>[] entries, CachePolicy policy, CancellationToken token, [NotNullWhen(true)] out KeyValuePair<TState, T?>[]? results)
        where TState : notnull
    {
        results = null;
        if (entries.Length == 0 || token.IsCancellationRequested || (policy.RehydrateEnabled == true && policy.Rehydrate is not null))
        {
            return false;
        }

        var found = new KeyValuePair<TState, T?>[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            if (ProbeLocal<T>(entries[i].Key, token, out var entry) != LocalProbe.Hit || !entry!.Found)
            {
                return false;
            }

            found[i] = new KeyValuePair<TState, T?>(entries[i].Value, entry.Value);
        }

        if (HasRepeatedState(entries))
        {
            return false;
        }

        results = found;
        return true;
    }

    /// <summary>Splits entries into state space and mapped-key space, both in first-occurrence order.</summary>
    private (TState[] States, int[] KeyIndexOfState, CacheKey[] CallerKeys, CacheEntryOptions[] Options, int[] FirstStateOfKey) DistinctEntries<T, TState>(
        KeyValuePair<CacheKey, TState>[] entries,
        DateTimeOffset? expiration,
        CancellationToken token)
        where TState : notnull
    {
        var states = new List<TState>(entries.Length);
        var keyIndexOfState = new List<int>(entries.Length);
        var callerKeys = new List<CacheKey>(entries.Length);
        var options = new List<CacheEntryOptions>(entries.Length);
        var firstStateOfKey = new List<int>(entries.Length);
        var seenStates = new HashSet<TState>(entries.Length);
        var keySlot = new Dictionary<CacheKey, int>(entries.Length);

        foreach (var entry in entries)
        {
            if (!seenStates.Add(entry.Value))
            {
                continue;
            }
            var entryOptions = _entryBuilder.BuildEntryOptions<T>(entry.Key, expiration, token);
            if (!keySlot.TryGetValue(entryOptions.CacheKey, out var slot))
            {
                slot = options.Count;
                options.Add(entryOptions);
                callerKeys.Add(entry.Key);
                keySlot[entryOptions.CacheKey] = slot;
                firstStateOfKey.Add(states.Count);
            }
            states.Add(entry.Value);
            keyIndexOfState.Add(slot);
        }

        return (states.ToArray(), keyIndexOfState.ToArray(), callerKeys.ToArray(), options.ToArray(), firstStateOfKey.ToArray());
    }

    private async ValueTask<bool> TryAddCoreAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);

        if (value is null && !_multiLayerCacheOptions.CacheNullValues)
        {
            LogTryAddSkippedUnrepresentableValue(Logged(options, typeof(T)));
            return false;
        }

        if (options.Expiration <= _clock.GetUtcNow())
        {
            LogTryAddSkippedExpiredEntry(Logged(options, typeof(T)), options.Expiration);
            return false;
        }

        var localMaxExpiration = policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration;
        if (localMaxExpiration is { } max && max <= TimeSpan.Zero)
        {
            LogTryAddSkippedNonPositiveLocalRetention(Logged(options, typeof(T)), max);
            return false;
        }

        return await TryAddUnderRunAsync(options, value, policy, localMaxExpiration).ConfigureAwait(false);
    }

    /// <summary>Takes the key's run for the add, so a miss does not generate the key, and a generator does not store over the added value, between the add's check and its write. A run that holds the key is waited for as long as the local lock would have been; one that still holds it then makes the add lose, as a lock not acquired in time did.</summary>
    private async ValueTask<bool> TryAddUnderRunAsync<T>(CacheEntryOptions options, T? value, CachePolicy policy, TimeSpan? localMaxExpiration)
    {
        var key = new InFlightKey(options.CacheKey.Name, typeof(T), null, null);
        if (!_generations.TryReserve(key, out var reservation)
            && (!await WaitForRunAsync(key, ResolveLocalLock(policy.Lock).Timeout, options.Token).ConfigureAwait(false) || !_generations.TryReserve(key, out reservation)))
        {
            LogTryAddKeyBusy(Logged(options, typeof(T)));
            return false;
        }

        var added = false;
        try
        {
            added = await TryAddUnderLocalLockAsync(options, value, policy, localMaxExpiration).ConfigureAwait(false);
            return added;
        }
        finally
        {
            // Callers that joined the add take its value, or take the key over when it lost.
            if (added)
            {
                reservation.Complete(_cacheEntryFactory.Create<T?>(value, options.Expiration));
            }
            else
            {
                reservation.Fail(new InFlightAbandonedException());
            }
        }
    }

    /// <summary>True when the run that held the key has ended, whether it stored or failed, and false when it still holds it after <paramref name="timeout"/>.</summary>
    private async ValueTask<bool> WaitForRunAsync(InFlightKey key, TimeSpan timeout, CancellationToken token)
    {
        if (!_generations.TryJoin(key, token, out var joined))
        {
            return true;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(BoundedTimeout(timeout));
        try
        {
            await joined.Result.WaitAsync(bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // The run failed, so it holds the key no longer.
            return true;
        }
        finally
        {
            joined.Withdraw();
            // Left behind on a timeout or a cancellation, the wait still ends with the run's failure: observed here, not reported unobserved.
            joined.Result.Forget();
        }
    }

    /// <summary>
    /// Probe the local tier, then let the L2 decide. The probe is what narrows a store that retains
    /// nothing — and therefore grants every caller a win — back to one winner per process; where the
    /// L2 does arbitrate, a local hit means the key was already claimed or read here, so reporting
    /// the loss early is both correct and a round-trip saved. It can cost a win the L2 would have
    /// granted, when the local copy outlived the shared one; that is the fail-closed direction the
    /// ambiguous <c>false</c> already covers.
    /// </summary>
    private async ValueTask<bool> TryAddUnderLocalLockAsync<T>(CacheEntryOptions options, T? value, CachePolicy policy, TimeSpan? localMaxExpiration)
    {
        if (_memoryCache.TryGetValue(options.CacheKey.Name, out _))
        {
            return false;
        }

        // Released before the event, so a slow one does not hold the key's background writes.
        var added = await _background.WriteAsync(
            options.CacheKey.Name,
            (Cache: this, Options: options, Value: value, Policy: policy, Max: localMaxExpiration),
            null,
            [AsyncMethodBuilder(typeof(Pooled<>))] static async ValueTask<bool> (s, _) =>
            {
                try
                {
                    return await s.Cache._innerCache.TryAddAsync<T?>(s.Options.CacheKey, s.Value, s.Options.Expiration, s.Policy, s.Options.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && s.Options.Token.IsCancellationRequested))
                {
                    s.Cache.LogInnerCacheTryAddError(ex, s.Cache.Logged(s.Options, typeof(T)));
                    return false;
                }
            },
            static (s, added, keeper) =>
            {
                if (added)
                {
                    try
                    {
                        keeper.Local(s.Options.CacheKey.Name, s, static x => x.Cache.MemorySet(x.Options, x.Value, x.Max));
                    }
                    catch (Exception ex)
                    {
                        s.Cache.LogTryAddLocalPropagationFailed(ex, s.Cache.Logged(s.Options, typeof(T)));
                    }
                }

                return added;
            },
            options.Token).ConfigureAwait(false);
        if (!added)
        {
            return false;
        }

        // Best-effort after the win: a loss reported here would strand the entry with no owner.
        try
        {
            if (!await _eventPublisher.CacheSetAsync(options, typeof(T)).ConfigureAwait(false))
            {
                LogTryAddBroadcastNotPublished(Logged(options, typeof(T)));
            }
        }
        catch (Exception ex)
        {
            LogTryAddLocalPropagationFailed(ex, Logged(options, typeof(T)));
        }

        return true;
    }

    private async ValueTask<bool> SetCoreAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        if (value is null && !_multiLayerCacheOptions.CacheNullValues)
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        LogReplacingCachedKey(Logged(cacheEntryOptions, typeof(T)));
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            return await Layered<T>().InternalSetAsync(cacheEntryOptions, value, innerCacheDisconnected, policy).ConfigureAwait(false);
        }

        if (policy.BackgroundDistributedWrite == true && _background.TryReserve())
        {
            return SetInBackground(cacheEntryOptions, value, policy);
        }

        // Written before the event, so a node it wakes reads the new value.
        var (written, kept, _) = await Layered<T>().SetInnerAsync(cacheEntryOptions, value, innerCacheDisconnected, policy, keepRefused: false).ConfigureAwait(false);
        return written && await Layered<T>().PublishSetAsync(cacheEntryOptions).ConfigureAwait(false) && kept;
    }

    private async ValueTask<bool> SetCoreAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var removeEntries = new List<CacheEntryOptions>();
        var setEntries = new List<CacheEntryValue<T>>();
        foreach (var keyValue in keyValues)
        {
            if (keyValue.Value is null && !_multiLayerCacheOptions.CacheNullValues)
            {
                removeEntries.Add(_entryBuilder.BuildEntryOptions<T>(keyValue.Key, token: token));
            }
            else
            {
                setEntries.Add(new (_entryBuilder.BuildEntryOptions<T>(keyValue.Key, expiration, token), keyValue.Value));
            }
        }

        if (removeEntries.Count > 0)
        {
            var result = await RemoveAsync<T>(removeEntries.ToArray(), token).ConfigureAwait(false);
            if (!result)
            {
                return false;
            }
        }

        var innerCacheDisconnected = GetInnerCacheDisconnected();
        var (written, kept) = await SetInnerAsync<T>(setEntries.ToArray(), innerCacheDisconnected, policy, keepRefused: false, token).ConfigureAwait(false);
        if (innerCacheDisconnected)
        {
            if (kept && _logger.IsEnabled(LogLevel.Trace))
            {
                LogSettingLocalOnlyForCacheKeys(Logged(setEntries.Select(o => o.CacheEntry).ToArray(), typeof(T)));
            }
            return kept;
        }

        // Announced even when the write reports failure: across Redis Cluster slots it can have written some keys, and a peer
        // that drops an unchanged copy only reads it again.
        var published = await PublishSetsAsync(setEntries).ConfigureAwait(false);
        return written && published && kept;
    }

    /// <summary>Every key is announced, even if one event is refused.</summary>
    private async ValueTask<bool> PublishSetsAsync<T>(List<CacheEntryValue<T>> setEntries)
    {
        var published = true;
        foreach (var cacheEntry in setEntries.Select(s => s.CacheEntry))
        {
            LogReplacingCachedKey(Logged(cacheEntry, typeof(T)));
            published &= await Layered<T>().PublishSetAsync(cacheEntry).ConfigureAwait(false);
        }

        return published;
    }

    private async ValueTask<bool> RefreshCoreAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        LogClearingCached(Logged(cacheEntryOptions, typeof(T)));
        LogRefreshingInnerCacheKey(Logged(cacheEntryOptions, typeof(T)), cacheEntryOptions.Expiration);
        try
        {
            // In the key's order first, so a background set issued meanwhile lands after this and keeps its local value.
            return await _background.WriteAsync(
                cacheEntryOptions.CacheKey.Name,
                (Cache: this, Options: cacheEntryOptions, Policy: policy, Token: token),
                static s => s.Cache.RemoveLocal(s.Options.CacheKey.Name),
                static async s =>
                {
                    var fired = await s.Cache._eventPublisher.CacheRefreshedAsync(s.Options, typeof(T)).ConfigureAwait(false);
                    return fired && await s.Cache._innerCache.RefreshAsync<T>(s.Options.CacheKey, s.Options.Expiration, s.Policy, s.Token).ConfigureAwait(false);
                },
                token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheRefreshError(ex, Logged(cacheKey, typeof(T)));
            return false;
        }
    }

    private async ValueTask<bool> RemoveAsync<T>(CacheEntryOptions options)
    {
        LogClearingLocalCached(Logged(options, typeof(T)));
        try
        {
            // Released before the event, so a slow one does not hold the key's background writes.
            var removed = await _background.WriteAsync(
                options.CacheKey.Name,
                (Cache: this, Options: options),
                static s => s.Cache.RemoveLocal(s.Options.CacheKey.Name),
                static s => s.Cache._innerCache.RemoveAsync<T>(s.Options.CacheKey, s.Options.Token),
                options.Token).ConfigureAwait(false);

            var eventFired = await _eventPublisher.CacheRemovedAsync(options, typeof(T)).ConfigureAwait(false);
            return removed && eventFired;
        }
        catch (OperationCanceledException) when (options.Token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogInnerCacheRemoveError(ex, Logged(options, typeof(T)));
            return false;
        }
    }

    private async ValueTask<bool> RemoveAsync<T>(CacheEntryOptions[] options, CancellationToken token = default)
    {
        try
        {
            // As the single-key remove does: the local entries go before the inner delete, so a failed one cannot leave them here.
            var removed = await _background.WriteAsync(
                options.Select(o => o.CacheKey.Name),
                (Cache: this, Options: options, Token: token),
                static s =>
                {
                    foreach (var option in s.Options)
                    {
                        s.Cache.RemoveLocal(option.CacheKey.Name);
                    }
                },
                static s => s.Cache._innerCache.RemoveAsync<T>(Array.ConvertAll(s.Options, static o => o.CacheKey), s.Token),
                token).ConfigureAwait(false);

            // Each key's invalidation is sent on its own, so one that fails does not keep the others from the other nodes.
            var published = true;
            foreach (var option in options)
            {
                try
                {
                    published &= await _eventPublisher.CacheRemovedAsync(option, typeof(T)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogInnerCacheRemoveError(ex, Logged(option, typeof(T)));
                    published = false;
                }
            }

            return removed && published;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                LogInnerCacheRemoveKeysError(ex, Logged(options, typeof(T)));
            }
            return false;
        }
    }

    private async ValueTask<KeyValuePair<CacheKey, T?>[]> GetInnerAsync<T>(CacheEntryOptions[] options, CachePolicy policy, CancellationToken token = default)
    {
        List<KeyValuePair<CacheKey, T?>> results = [];
        List<CacheEntryOptions> cacheEntriesToFetch = [];
        foreach (var option in options)
        {
            if (_memoryCache.TryGetValue<ICacheEntry<T>>(option.CacheKey.Name, out var entry))
            {
                LogFoundLocal(Logged(option, typeof(T)));
                if (_connectionState.IsConnected)
                {
                    results.Add(new KeyValuePair<CacheKey, T?>(option.CacheKey, entry!.Value));
                }
                else if (_useLocalOnlyWhenDisconnected)
                {
                    LogUsingPrimaryOnlyWhenDisconnected(Logged(option, typeof(T)));
                    results.Add(new KeyValuePair<CacheKey, T?>(option.CacheKey, entry!.Value));
                }
                else
                {
                    LogReturningDefaultDisconnected(Logged(option, typeof(T)));
                    RemoveLocal(option.CacheKey.Name);
                }
            }
            else
            {
                cacheEntriesToFetch.Add(option);
            }
        }

        var keys = cacheEntriesToFetch.Select(c => c.CacheKey).ToArray();
        if (keys.Length == 0)
        {
            return results.ToArray();
        }

        var marks = MarkInnerReads(keys);
        var fetched = await _innerCache.GetCacheEntriesAsync<T>(keys, policy, token).ConfigureAwait(false);

        for (int i = 0; i < keys.Length; i++)
        {
            var entry = fetched[i].Value;
            var key = fetched[i].Key;
            results.Add(new KeyValuePair<CacheKey, T?>(key, entry.Value));

            if (!entry.Found)
            {
                continue;
            }

            LogFoundInnerCacheCopy(LoggedComposed(key, typeof(T)));
            var option = cacheEntriesToFetch[i];
            option.Expiration = entry.Expiration;
            Layered<T>().FillFromInnerRead(option, marks?[i] ?? 0, entry.Value, policy);
        }

        return results.ToArray();
    }

    private async ValueTask<T?> GetInnerAsync<T>(CacheEntryOptions options, CachePolicy policy)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<T>>(options.CacheKey.Name, out var entry))
        {
            LogFoundLocal(Logged(options, typeof(T)));
            if(_connectionState.IsConnected)
            {
                return entry!.Value;
            }
            else if (_useLocalOnlyWhenDisconnected)
            {
                LogUsingPrimaryOnlyWhenDisconnected(Logged(options, typeof(T)));
                return entry!.Value;
            }
            else
            {
                LogReturningDefaultDisconnected(Logged(options, typeof(T)));
                RemoveLocal(options.CacheKey.Name);
                return default;
            }
        }

        var fetched = await Layered<T>().FetchInnerAsync(options, policy).ConfigureAwait(false);
        return fetched.Found ? fetched.Value : default;
    }

    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesInnerAsync<T>(CacheEntryOptions[] options, CachePolicy policy, CancellationToken token = default)
    {
        var results = new KeyValuePair<CacheKey, ICacheEntry<T?>>[options.Length];
        List<int> missIndices = [];
        List<CacheEntryOptions> cacheEntriesToFetch = [];
        for (int i = 0; i < options.Length; i++)
        {
            var option = options[i];
            if (_memoryCache.TryGetValue<ICacheEntry<T?>>(option.CacheKey.Name, out var entry))
            {
                LogFoundLocal(Logged(option, typeof(T)));
                if (_connectionState.IsConnected || _useLocalOnlyWhenDisconnected)
                {
                    if (!_connectionState.IsConnected)
                    {
                        LogUsingPrimaryOnlyWhenDisconnected(Logged(option, typeof(T)));
                    }
                    results[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(option.CacheKey, entry!);
                    continue;
                }

                LogReturningDefaultDisconnected(Logged(option, typeof(T)));
                RemoveLocal(option.CacheKey.Name);
                results[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(option.CacheKey, _cacheEntryFactory.Create<T?>(default, DateTimeOffset.MinValue));
                continue;
            }

            missIndices.Add(i);
            cacheEntriesToFetch.Add(option);
        }

        if (cacheEntriesToFetch.Count == 0)
        {
            return results;
        }

        var keys = cacheEntriesToFetch.Select(c => c.CacheKey).ToArray();
        var marks = MarkInnerReads(keys);
        var fetched = await _innerCache.GetCacheEntriesAsync<T>(keys, policy, token).ConfigureAwait(false);

        for (int j = 0; j < keys.Length; j++)
        {
            var resultIndex = missIndices[j];
            var entry = fetched[j].Value;
            var key = fetched[j].Key;
            results[resultIndex] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(key, entry);

            if (!entry.Found)
            {
                continue;
            }

            LogFoundInnerCacheCopy(LoggedComposed(key, typeof(T)));
            var option = cacheEntriesToFetch[j];
            option.Expiration = entry.Expiration;
            Layered<T>().FillFromInnerRead(option, marks?[j] ?? 0, entry.Value, policy);
        }

        return results;
    }

    private async ValueTask<ICacheEntry<T?>> GetCacheEntryInnerAsync<T>(CacheEntryOptions options, CachePolicy policy)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<T?>>(options.CacheKey.Name, out var entry))
        {
            LogFoundLocal(Logged(options, typeof(T)));
            if (_connectionState.IsConnected || _useLocalOnlyWhenDisconnected)
            {
                if (!_connectionState.IsConnected)
                {
                    LogUsingPrimaryOnlyWhenDisconnected(Logged(options, typeof(T)));
                }
                return entry!;
            }

            LogReturningDefaultDisconnected(Logged(options, typeof(T)));
            RemoveLocal(options.CacheKey.Name);
            return _cacheEntryFactory.Create<T?>(default, DateTimeOffset.MinValue);
        }

        return await Layered<T>().FetchInnerAsync(options, policy).ConfigureAwait(false);
    }

    private long[]? MarkInnerReads(CacheKey[] keys) => _background.Active ? Array.ConvertAll(keys, k => MarkInnerRead(k.Name)) : null;

    private async ValueTask<bool> InternalSetAsync<T>(CacheEntryValue<T>[] cacheEntries, bool innerCacheDisconnected, CachePolicy policy, bool keepRefused, CancellationToken token) =>
        (await SetInnerAsync(cacheEntries, innerCacheDisconnected, policy, keepRefused, token).ConfigureAwait(false)).Kept;

    private async ValueTask<(bool Written, bool Kept)> SetInnerAsync<T>(CacheEntryValue<T>[] cacheEntries, bool innerCacheDisconnected, CachePolicy policy, bool keepRefused, CancellationToken token)
    {
        try
        {
            var cacheKeyValuePairs = cacheEntries.Select(c => new KeyValuePair<CacheKey, T?>(c.CacheEntry.CacheKey, c.Value)).ToArray();

            if (innerCacheDisconnected)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    LogSettingLocalOnlyForCacheKeys(Logged(cacheEntries.Select(o => o.CacheEntry).ToArray(), typeof(T)));
                }
                return (false, Array.TrueForAll(cacheEntries, e => ApplyLocal(e.CacheEntry.CacheKey.Name, (Cache: this, Entry: e, Max: policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected), static s => s.Cache.MemorySet(s.Entry.CacheEntry, s.Entry.Value, s.Max))));
            }

            return await _background.WriteAsync(
                cacheEntries.Select(c => c.CacheEntry.CacheKey.Name),
                (Cache: this, Entries: cacheEntries, Pairs: cacheKeyValuePairs, Policy: policy, KeepRefused: keepRefused, Token: token),
                null,
                static (s, _) => s.Cache.SetBatchAsync(s.Entries, s.Pairs, s.Policy, s.KeepRefused, s.Token),
                static (s, set, keeper) =>
                {
                    // Across Redis Cluster slots a write that reports failure can have written some keys, and this node does not hear
                    // its own events; dropped inside the turn, so no other write of these keys lands in between.
                    if (!set && !s.KeepRefused)
                    {
                        s.Cache.RemoveLocal(s.Entries);
                    }

                    return (set, s.Cache.KeepAfterInnerWrite(set, s.KeepRefused, s.Policy, (s.Cache, s.Entries, Keeper: keeper), static (x, max) => Array.TrueForAll(x.Entries, e => x.Keeper.Local(e.CacheEntry.CacheKey.Name, (x.Cache, e, max), static y => y.Cache.MemorySet(y.e.CacheEntry, y.e.Value, y.max))), s.Token));
                },
                token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogBatchSetError(ex, cacheEntries);
            return (false, false);
        }
    }

    private async ValueTask<bool> SetBatchAsync<T>(CacheEntryValue<T>[] entries, KeyValuePair<CacheKey, T?>[] pairs, CachePolicy policy, bool keepRefused, CancellationToken token)
    {
        try
        {
            // One deadline for the batch, taken from the first entry — every entry in a batch is
            // built from the same resolved expiration. With no entries there is nothing to date,
            // so the write inherits the policy.
            return entries.Length > 0
                ? await _innerCache.SetAsync<T?>(pairs, entries[0].CacheEntry.Expiration, policy, token).ConfigureAwait(false)
                : await _innerCache.SetAsync<T?>(pairs, policy, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (keepRefused && !(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            LogBatchSetError(ex, entries);
            return false;
        }
    }

    private void RemoveLocal<T>(CacheEntryValue<T>[] entries)
    {
        foreach (var entry in entries)
        {
            RemoveLocal(entry.CacheEntry.CacheKey.Name);
        }
    }

    private void LogBatchSetError<T>(Exception ex, CacheEntryValue<T>[] cacheEntries)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            LogInnerCacheSetKeysError(ex, Logged(cacheEntries.Select(o => o.CacheEntry).ToArray(), typeof(T)));
        }
    }

    private bool MemorySet<T>(CacheEntryOptions options, T value, TimeSpan? maxExpiration, ICacheEntry<T>? item = null)
    {
        item ??= _cacheEntryFactory.Create(value, options.Expiration);
        return _localMemorySetter.Set(options, item, typeof(T), maxExpiration);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Batch cache missed. generating {Count} keys for {CacheKey}")]
    private partial void LogBatchCacheMissed(LoggedKeys cacheKey, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {CacheKey}: a null value cannot be represented unless CacheNullValues is on.")]
    private partial void LogTryAddSkippedUnrepresentableValue(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache TryAdd for cacheKey {CacheKey}")]
    private partial void LogInnerCacheTryAddError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TryAdd won cacheKey {CacheKey} in the inner cache but could not broadcast or populate the local tier. The add still stands.")]
    private partial void LogTryAddLocalPropagationFailed(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TryAdd for {CacheKey} reported not-added: a generator for the key was still running after Lock.LocalSingleFlightTimeout, and without waiting for it two in-process callers could both be told they added the key, or the generated value could replace the added one. Raise Lock.LocalSingleFlightTimeout if this key is contended.")]
    private partial void LogTryAddKeyBusy(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {CacheKey}: the requested expiration {Expiration} is not in the future, so the entry would retain nothing.")]
    private partial void LogTryAddSkippedExpiredEntry(LoggedKey cacheKey, DateTimeOffset expiration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {CacheKey}: the effective local retention {LocalMaxExpiration} is not positive, and on this provider it is the only retention.")]
    private partial void LogTryAddSkippedNonPositiveLocalRetention(LoggedKey cacheKey, TimeSpan localMaxExpiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TryAdd won cacheKey {CacheKey} but the invalidation broadcast reported not-published. The add still stands; peers may serve a stale copy until it expires.")]
    private partial void LogTryAddBroadcastNotPublished(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replacing cached key {CacheKey}")]
    private partial void LogReplacingCachedKey(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Setting local only for cacheKeys {CacheKeys}")]
    private partial void LogSettingLocalOnlyForCacheKeys(LoggedKeys cacheKeys);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing cached. Key {CacheKey}")]
    private partial void LogClearingCached(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing inner cache key {CacheKey} at expiration {Expiration}")]
    private partial void LogRefreshingInnerCacheKey(LoggedKey cacheKey, DateTimeOffset? expiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache refresh value for cacheKey {CacheKey}")]
    private partial void LogInnerCacheRefreshError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache contains for cacheKey {CacheKey}")]
    private partial void LogInnerCacheContainsError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing local cached. cacheKey {CacheKey}")]
    private partial void LogClearingLocalCached(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache remove cacheKey {CacheKey}")]
    private partial void LogInnerCacheRemoveError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache remove cacheKeys {CacheKeys}")]
    private partial void LogInnerCacheRemoveKeysError(Exception ex, LoggedKeys cacheKeys);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found local. {CacheKey}")]
    private partial void LogFoundLocal(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Using primary only when disconnected. Returning local for cacheKey {CacheKey}")]
    private partial void LogUsingPrimaryOnlyWhenDisconnected(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Returning default for cacheKey {CacheKey}")]
    private partial void LogReturningDefaultDisconnected(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found inner cache copy at cacheKey {CacheKey}")]
    private partial void LogFoundInnerCacheCopy(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set value for {CacheKeys}")]
    private partial void LogInnerCacheSetKeysError(Exception ex, LoggedKeys cacheKeys);

    /// <summary>What the batch rehydrate write needs to know about one state.</summary>
    private readonly record struct RehydrateTarget(CacheEntryOptions Options, DateTimeOffset Expiration, CacheKey CallerKey);

    private readonly struct CacheEntryValue<T>(CacheEntryOptions cacheEntry, T? value)
    {
        public CacheEntryOptions CacheEntry { get; init; } = cacheEntry;
        public T? Value { get; init; } = value;
    }

    private readonly record struct Miss<T>(CacheKey Key, CacheEntryOptions Options, Func<CancellationToken, Task<T?>> Generator, TimeSpan Duration, TimeSpan? RehydrateJitter, CachePolicy Policy, long Seen);

    private sealed record BatchRun(int Index, IInFlightReservation<object?> Run);

    private sealed record BatchJoin<T>(int Index, InFlight<InFlightKey, object?>.Joined Join, Task<ICacheEntry<T?>> Run);
}
