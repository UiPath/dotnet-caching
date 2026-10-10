using System.Collections.Concurrent;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed partial class MultilayerCache : MultilayerCacheBase, ICache, ISpanKeyCache, IStatefulCache, IGeneratedExpirationCache
{
    private readonly ICache _innerCache;
    private readonly InFlight<InFlightKey, object?> _innerReads = new();
    private readonly InFlight<InFlightKey, object?> _generations = new();
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
        ILocalLock localLock,
        IDistributedLock distributedLock,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        ILogger logger,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
        : base(cacheName, innerCache, memoryCacheFactory, topicFactory, cacheEventFactory, telemetryProvider, multiLayerCacheOptions, memoryCacheOptions, cacheOptions, localLock, distributedLock, policyFactory, clock, logger, keyMaskingPolicy)
    {
        _innerCache = innerCache;
        var cacheKeyStrategy = _multiLayerCacheOptions.CacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        var topicKeyStrategy = _multiLayerCacheOptions.TopicKeyStrategy ?? new DefaultTopicKeyStrategy(cacheOptions.Separator);
        _entryBuilder = new CacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _localMemorySetter = new LocalMemorySetter(cacheName, changeTokenFactory, _topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions, memoryCacheOptions, telemetryProvider, _masker, !_clearLocalOnReconnect);
    }

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
        // One floored resolution for the write and the rehydrate threshold; only the write is jittered.
        var duration = ResolveDuration(policy);
        var writeExpiration = _clock.ToDateTimeOffset(ApplyJitter(duration, policy.JitterMaxDuration));
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, policy.JitterMaxDuration, policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return TryGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value)
            ? new ValueTask<T?>(value)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return TryGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value)
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return TryGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value)
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var duration = ResolveDuration(policy);
        var writeExpiration = _clock.ToDateTimeOffset(ApplyJitter(duration, policy.JitterMaxDuration));
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, policy.JitterMaxDuration, policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return TryGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value)
            ? new ValueTask<T?>(value)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return TryGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value)
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return TryGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value)
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
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
        return GetOrAddBatchInternalAsync<T, TState>(entries, generator, writeExpiration, duration, policy.JitterMaxDuration, policy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddBatchInternalAsync<T, TState>(entries, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddBatchInternalAsync<T, TState>(entries, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddGeneratedAsync(cacheKey, generator, policy ?? _defaultPolicy, token);
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

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);

        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

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

    private ValueTask<T?> GetOrAddInternalAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, CancellationToken token)
    {
        // Not async: the miss path's lock delegates capture these parameters, and an async method would build that closure on a hit too.
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T?>(cacheKey, token, out var local) && local.Found)
        {
            TryRehydrate(cacheKey, local.Expiration, local.Value, generator, policy, effectiveDuration, rehydrateJitter);
            return new ValueTask<T?>(local.Value);
        }

        return GetOrAddCoreAsync(cacheKey, generator, expiration, effectiveDuration, rehydrateJitter, policy, token);
    }

    private ValueTask<T?> GetOrAddInternalAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, CancellationToken token)
    {
        // Binds the state to a delegate only where one is needed, so a hit builds no closure.
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T?>(cacheKey, token, out var local) && local.Found)
        {
            if (ShouldRehydrate(local.Value, policy, effectiveDuration))
            {
                TriggerRehydrate(cacheKey, local.Expiration, Bind(state, generator), policy, effectiveDuration, rehydrateJitter);
            }
            return new ValueTask<T?>(local.Value);
        }

        return GetOrAddCoreAsync(cacheKey, Bind(state, generator), expiration, effectiveDuration, rehydrateJitter, policy, token);
    }

    private async ValueTask<T?> GetOrAddCoreAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);

        var entry = await GetCacheEntryInnerAsync<T>(cacheEntryOptions, policy).ConfigureAwait(false);
        if (entry.Found)
        {
            TryRehydrate(cacheKey, entry.Expiration, entry.Value, generator, policy, effectiveDuration, rehydrateJitter);
            return entry.Value;
        }

        var result = await GetOrGenerateEntryAsync<T, Func<CancellationToken, Task<T?>>>(cacheEntryOptions, generator, policy, static (cache, options, g, p, scope) => cache.RunGeneratorAndStoreEntryAsync(options, g, p, scope), token).ConfigureAwait(false);
        return result.Value;
    }

    private void TryRehydrate<T>(CacheKey originalCacheKey, DateTimeOffset entryExpiration, T? currentValue, Func<CancellationToken, Task<T?>> generator, CachePolicy policy, TimeSpan duration, TimeSpan? rehydrateJitter)
    {
        if (ShouldRehydrate(currentValue, policy, duration))
        {
            TriggerRehydrate(originalCacheKey, entryExpiration, generator, policy, duration, rehydrateJitter);
        }
    }

    private bool ShouldRehydrate<T>(T? currentValue, CachePolicy policy, TimeSpan duration)
    {
        if (policy.RehydrateEnabled != true || policy.Rehydrate is null)
        {
            return false;
        }
        if (currentValue is null && _multiLayerCacheOptions.CacheNullValues)
        {
            return false;
        }
        return duration > TimeSpan.Zero && duration != TimeSpan.MaxValue;
    }

    /// <summary>Hands the rehydration to the coordinator; apart from <see cref="TryRehydrate{T}"/> so its early returns do not allocate the closure.</summary>
    private void TriggerRehydrate<T>(CacheKey originalCacheKey, DateTimeOffset entryExpiration, Func<CancellationToken, Task<T?>> generator, CachePolicy policy, TimeSpan duration, TimeSpan? rehydrateJitter)
    {
        _rehydrator.TryTrigger(
            originalCacheKey,
            entryExpiration,
            policy,
            duration,
            kind: "cache",
            rehydrateAsync: async ct =>
            {
                var newValue = await generator(ct).ConfigureAwait(false);
                if (newValue is null && !_multiLayerCacheOptions.CacheNullValues)
                {
                    return;
                }
                // Factory transitions to null: preserve the original deadline so the null doesn't get a fresh TTL window.
                var rehydrateExpiration = newValue is null
                    ? entryExpiration
                    : _clock.ToDateTimeOffset(ApplyJitter(duration, rehydrateJitter));
                var rehydrateOptions = _entryBuilder.BuildEntryOptions<T>(originalCacheKey, rehydrateExpiration, ct);
                var innerCacheDisconnected = GetInnerCacheDisconnected();
                var fired = innerCacheDisconnected || await _eventPublisher.CacheSetAsync(rehydrateOptions, typeof(T)).ConfigureAwait(false);
                var written = fired && await InternalSetAsync(rehydrateOptions, newValue, innerCacheDisconnected, policy).ConfigureAwait(false);
                if (!written)
                {
                    throw new RehydrateWriteFailedException(originalCacheKey.Name);
                }
            },
            entryType: typeof(T));
    }

    /// <summary>Coalesces the rehydration of every hit past its threshold into one background generator call.</summary>
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
        CancellationToken token)
    {
        var lifetime = new LockLifetime();
        return RunUnderLocksAsync(
            options.CacheKey,
            (Cache: this, Options: options, Generator: generator, Policy: policy, Generate: generate, Lifetime: lifetime),
            static s => s.Cache.GetCacheEntryInnerAsync<T>(s.Options, s.Policy),
            static (ICacheEntry<T?> e) => e.Found,
            static (s, ct) => s.Cache.RunSharedAsync<T, TGenerator>(s.Options, s.Generator, s.Policy, s.Generate, s.Lifetime, ct),
            token,
            policy.Lock,
            lifetime);
    }

    /// <summary>Runs the generation as one run per key: a caller whose wait for the local lock timed out, or that arrives while it runs, joins it, or the batch run covering its key, rather than starting another. Only the generator runs on the shared token; the write keeps the starting caller's, so that caller's cancellation is still told from a refusal. The starting caller's locks stay held until the run ends, even if that caller stops waiting first.</summary>
    private async ValueTask<ICacheEntry<T?>> RunSharedAsync<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        LockLifetime lifetime,
        CancellationToken token)
    {
        if (!ResolveLocalLock(policy.Lock).Enabled)
        {
            return (ICacheEntry<T?>)(await generate(this, options, generator, policy, new GenerationScope(token, null)).ConfigureAwait(false))!;
        }

        var key = new InFlightKey(options.CacheKey.Name, typeof(T), null, null);
        while (true)
        {
            try
            {
                return (ICacheEntry<T?>)(await _generations.RunAsync(
                    key,
                    (Cache: this, Options: options, Generator: generator, Policy: policy, Generate: generate, Lifetime: lifetime),
                    static (state, run) =>
                    {
                        state.Lifetime.Run = run;

                        // A run that ended in this process since the caller read the key left its value in the local tier, which it writes before it leaves the table.
                        return state.Cache.TryGetLocalEntry<T>(state.Options, out var current)
                            ? new ValueTask<object?>(current)
                            : state.Generate(state.Cache, state.Options, state.Generator, state.Policy, new GenerationScope(run.Token, run));
                    },
                    token).ConfigureAwait(false))!;
            }
            catch (InFlightAbandonedException)
            {
                // The batch that held the key was cancelled: the key is up for taking again.
            }
        }
    }

    private async ValueTask<object?> RunGeneratorAndStoreEntryAsync<T>(CacheEntryOptions cacheEntryOptions, Func<CancellationToken, Task<T?>> generator, CachePolicy policy, GenerationScope scope)
    {
        LogCacheMissed(Logged(cacheEntryOptions, typeof(T)));
        var ret = await InvokeFactoryAsync(cacheEntryOptions.CacheKey, generator, policy.FactoryTimeout, scope.Token).ConfigureAwait(false);
        return await StoreGeneratedEntryAsync(cacheEntryOptions, ret, policy, scope).ConfigureAwait(false);
    }

    private async ValueTask<ICacheEntry<T?>> StoreGeneratedEntryAsync<T>(CacheEntryOptions cacheEntryOptions, T? ret, CachePolicy policy, GenerationScope scope)
    {
        // A generator that ignores its token can finish after every caller left and a newer run took the key: its value must not replace theirs.
        if ((ret is not null || _multiLayerCacheOptions.CacheNullValues) && !scope.IsAbandoned)
        {
            var innerCacheDisconnected = GetInnerCacheDisconnected();
            await InternalSetAsync(cacheEntryOptions, ret, innerCacheDisconnected, policy, keepRefused: true).ConfigureAwait(false);
        }
        return _cacheEntryFactory.Create<T?>(ret, cacheEntryOptions.Expiration);
    }

    private async ValueTask<T?> GetOrAddGeneratedAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, GetExpiration(policy), token);

        var entry = await GetCacheEntryInnerAsync<T>(cacheEntryOptions, policy).ConfigureAwait(false);
        if (entry.Found)
        {
            return entry.Value;
        }

        var result = await GetOrGenerateEntryAsync<T, Func<CancellationToken, Task<GeneratedValue<T>>>>(cacheEntryOptions, generator, policy, static (cache, options, g, p, scope) => cache.RunGeneratedAndStoreEntryAsync(options, g, p, scope), token).ConfigureAwait(false);
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

    private async ValueTask<KeyValuePair<TState, T?>[]> GetOrAddBatchInternalAsync<T, TState>(
        KeyValuePair<CacheKey, TState>[] entries,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        DateTimeOffset? expiration,
        TimeSpan effectiveDuration,
        TimeSpan? rehydrateJitter,
        CachePolicy policy,
        CancellationToken token)
        where TState : notnull
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(entries);

        var (states, keyIndexOfState, callerKeys, options, firstStateOfKey) = DistinctEntries<T, TState>(entries, expiration, token);
        if (states.Length == 0)
        {
            return [];
        }

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

        if (hits.Count > 0)
        {
            TryRehydrateBatch(hits, generator, policy, effectiveDuration, rehydrateJitter);
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

        var lockKey = CompositeCacheKey.For(Array.ConvertAll(missOptions, o => o.CacheKey));

        var latest = missProbe;
        var resolved = await RunUnderLocksAsync(
            lockKey,
            async () =>
            {
                latest = await GetCacheEntriesInnerAsync<T>(missOptions, policy, token).ConfigureAwait(false);
                return latest;
            },
            probed => Array.TrueForAll(probed, e => e.Value.Found),
            ct => RunBatchSharedAsync(missOptions, missStates, latest, generator, policy, ct),
            token,
            policyLock: policy.Lock).ConfigureAwait(false);

        for (var i = 0; i < missIndices.Count; i++)
        {
            values[missIndices[i]] = resolved[i].Value.Value;
        }

        return Project(states, keyIndexOfState, values);
    }

    /// <summary>Joins the keys whose generator run is in flight, a single-key run or another batch, and runs one batch generator for the rest, which others in turn can join.</summary>
    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> RunBatchSharedAsync<T, TState>(
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        CancellationToken token)
        where TState : notnull
    {
        if (!ResolveLocalLock(policy.Lock).Enabled)
        {
            return await RunBatchGeneratorAndStoreAsync(missOptions, missStates, probe, generator, policy, token).ConfigureAwait(false);
        }

        var result = (KeyValuePair<CacheKey, ICacheEntry<T?>>[])probe.Clone();
        var owned = new List<BatchRun>();
        var joined = new List<BatchJoin<T>>();
        try
        {
            for (var i = 0; i < missOptions.Length; i++)
            {
                if (!probe[i].Value.Found)
                {
                    ReserveOrJoin(i, new InFlightKey(missOptions[i].CacheKey.Name, typeof(T), null, null), owned, joined, token);
                }
            }

            if (owned.Count > 0)
            {
                // Runs that ended in this process since the caller probed left their values in the local tier, which they write before they leave the table.
                var current = owned.Select(o => TryGetLocalEntry<T>(missOptions[o.Index], out var local) ? new KeyValuePair<CacheKey, ICacheEntry<T?>>(missOptions[o.Index].CacheKey, local) : probe[o.Index]).ToArray();
                var produced = await RunBatchGeneratorAndStoreAsync(
                    owned.Select(o => missOptions[o.Index]).ToArray(),
                    owned.Select(o => missStates[o.Index]).ToArray(),
                    current,
                    generator,
                    policy,
                    token).ConfigureAwait(false);
                for (var i = 0; i < owned.Count; i++)
                {
                    result[owned[i].Index] = produced[i];
                    owned[i].Run.Complete(produced[i].Value);
                }
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

        if (abandoned.Count > 0)
        {
            var retryOptions = abandoned.Select(i => missOptions[i]).ToArray();
            var fresh = await GetCacheEntriesInnerAsync<T>(retryOptions, policy, token).ConfigureAwait(false);
            var again = await RunBatchSharedAsync(retryOptions, abandoned.Select(i => missStates[i]).ToArray(), fresh, generator, policy, token).ConfigureAwait(false);
            for (var i = 0; i < abandoned.Count; i++)
            {
                result[abandoned[i]] = again[i];
            }
        }

        return result;
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

        var localLock = await AcquireLocalLockAsync(options.CacheKey, policy.Lock, options.Token).ConfigureAwait(false);
        if (localLock is null)
        {
            LogTryAddLocalLockUnavailable(Logged(options, typeof(T)));
            return false;
        }

        using (localLock)
        {
            return await TryAddUnderLocalLockAsync(options, value, policy, localMaxExpiration).ConfigureAwait(false);
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

        bool added;
        try
        {
            added = await _innerCache.TryAddAsync<T?>(options.CacheKey, value, options.Expiration, policy, options.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && options.Token.IsCancellationRequested))
        {
            LogInnerCacheTryAddError(ex, Logged(options, typeof(T)));
            return false;
        }

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

        try
        {
            MemorySet(options, value, localMaxExpiration);
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
            LogSettingLocalOnly(Logged(cacheEntryOptions, typeof(T)));
            return await InternalSetAsync(cacheEntryOptions, value, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
        else
        {
            var fired = await _eventPublisher.CacheSetAsync(cacheEntryOptions, typeof(T)).ConfigureAwait(false);
            return fired && await InternalSetAsync(cacheEntryOptions, value, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
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
        var internalSetResult = await InternalSetAsync<T>(setEntries.ToArray(), innerCacheDisconnected, policy, keepRefused: false, token).ConfigureAwait(false);
        if (!internalSetResult)
        {
            return false;
        }

        if (innerCacheDisconnected)
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                LogSettingLocalOnlyForCacheKeys(Logged(setEntries.Select(o => o.CacheEntry).ToArray(), typeof(T)));
            }
            return true;
        }

        foreach (var cacheEntry in setEntries.Select(s => s.CacheEntry))
        {
            LogReplacingCachedKey(Logged(cacheEntry, typeof(T)));
            var fired = await _eventPublisher.CacheSetAsync(cacheEntry, typeof(T)).ConfigureAwait(false);
            if (!fired)
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask<bool> RefreshCoreAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        LogClearingCached(Logged(cacheEntryOptions, typeof(T)));
        _memoryCache.Remove(cacheEntryOptions.CacheKey.Name);
        LogRefreshingInnerCacheKey(Logged(cacheEntryOptions, typeof(T)), cacheEntryOptions.Expiration);
        try
        {
            var fired = await _eventPublisher.CacheRefreshedAsync(cacheEntryOptions, typeof(T)).ConfigureAwait(false);
            return fired && await _innerCache.RefreshAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration, policy, token).ConfigureAwait(false);
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
            _memoryCache.Remove(options.CacheKey.Name);
            var removed = await _innerCache.RemoveAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);
            var eventFired = await _eventPublisher.CacheRemovedAsync(options, typeof(T)).ConfigureAwait(false);
            return removed && eventFired;
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
            var removeInnerResult = await _innerCache.RemoveAsync<T>(options.Select(o => o.CacheKey).ToArray(), token).ConfigureAwait(false);
            if (!removeInnerResult)
            {
                return false;
            }

            foreach (var option in options)
            {
                _memoryCache.Remove(option.CacheKey.Name);
            }

            foreach (var option in options)
            {
                var removedEventPublished = await _eventPublisher.CacheRemovedAsync(option, typeof(T)).ConfigureAwait(false);
                if (!removedEventPublished)
                {
                    return false;
                }
            }

            return true;
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
                    _memoryCache.Remove(option.CacheKey.Name);
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
            MemorySet(option, entry.Value, LocalLifetime(policy));
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
                _memoryCache.Remove(options.CacheKey.Name);
                return default;
            }
        }

        var fetched = await FetchInnerAsync<T>(options, policy).ConfigureAwait(false);
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
                _memoryCache.Remove(option.CacheKey.Name);
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
            MemorySet(option, entry.Value, LocalLifetime(policy));
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
            _memoryCache.Remove(options.CacheKey.Name);
            return _cacheEntryFactory.Create<T?>(default, DateTimeOffset.MinValue);
        }

        return await FetchInnerAsync<T>(options, policy).ConfigureAwait(false);
    }

    /// <summary>Reads the inner tier and keeps a hit locally; concurrent reads of one key share one inner read. The null inner tier has nothing to share or keep.</summary>
    private ValueTask<ICacheEntry<T?>> FetchInnerAsync<T>(CacheEntryOptions options, CachePolicy policy) =>
        _innerCache is NullCache
            ? _innerCache.GetCacheEntryAsync<T>(options.CacheKey, policy, options.Token)
            : FetchSharedInnerAsync<T>(options, policy);

    private async ValueTask<ICacheEntry<T?>> FetchSharedInnerAsync<T>(CacheEntryOptions options, CachePolicy policy) =>
        (ICacheEntry<T?>)(await _innerReads.RunAsync(
            new InFlightKey(options.CacheKey.Name, typeof(T), policy.LocalExpiration, policy.LocalExpirationDisconnected),
            (Cache: this, Options: options, Policy: policy),
            static (state, run) => state.Cache.FetchAndKeepAsync<T>(state.Options.Token == run.Token ? state.Options : state.Options with { Token = run.Token }, state.Policy, run),
            options.Token).ConfigureAwait(false))!;

    private async ValueTask<object?> FetchAndKeepAsync<T>(CacheEntryOptions options, CachePolicy policy, IInFlightRun run)
    {
        var fetched = await _innerCache.GetCacheEntryAsync<T>(options.CacheKey, policy, options.Token).ConfigureAwait(false);
        if (fetched.Found)
        {
            // Committed only while a caller still waits, so a read every caller left cannot overwrite a fresh one.
            run.TryCommit((Cache: this, Options: options, Policy: policy, Fetched: fetched), static s => s.Cache.Keep(s.Options, s.Policy, s.Fetched));
        }

        return fetched;
    }

    private void Keep<T>(CacheEntryOptions options, CachePolicy policy, ICacheEntry<T?> fetched)
    {
        LogFoundInnerCacheCopy(Logged(options, typeof(T)));
        options.Expiration = fetched.Expiration;
        MemorySet(options, fetched.Value, LocalLifetime(policy));
    }

    private async ValueTask<bool> InternalSetAsync<T>(CacheEntryOptions options, T? value, bool innerCacheDisconnected, CachePolicy policy, bool keepRefused = false)
    {
        try
        {
            if (innerCacheDisconnected)
            {
                LogSettingLocalOnly(Logged(options, typeof(T)));
                return MemorySet(options, value, policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected);
            }

            bool ret;
            try
            {
                ret = await _innerCache.SetAsync<T?>(options.CacheKey, value, options.Expiration, policy, options.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (keepRefused && !(ex is OperationCanceledException && options.Token.IsCancellationRequested))
            {
                LogInnerCacheSetError(ex, Logged(options, typeof(T)));
                ret = false;
            }

            return KeepAfterInnerWrite(ret, keepRefused, policy, (Cache: this, Options: options, Value: value), static (s, max) => s.Cache.MemorySet(s.Options, s.Value, max), options.Token);
        }
        catch (Exception ex)
        {
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
            return false;
        }
    }

    private async ValueTask<bool> InternalSetAsync<T>(CacheEntryValue<T>[] cacheEntries, bool innerCacheDisconnected, CachePolicy policy, bool keepRefused, CancellationToken token)
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
                return MemorySet(cacheEntries, policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected);
            }

            bool set;
            try
            {
                // One deadline for the batch, taken from the first entry — every entry in a batch is
                // built from the same resolved expiration. With no entries there is nothing to date,
                // so the write inherits the policy.
                set = cacheEntries.Length > 0
                    ? await _innerCache.SetAsync<T?>(cacheKeyValuePairs, cacheEntries[0].CacheEntry.Expiration, policy, token).ConfigureAwait(false)
                    : await _innerCache.SetAsync<T?>(cacheKeyValuePairs, policy, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (keepRefused && !(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                LogBatchSetError(ex, cacheEntries);
                set = false;
            }

            return KeepAfterInnerWrite(set, keepRefused, policy, (Cache: this, Entries: cacheEntries), static (s, max) => s.Cache.MemorySet(s.Entries, max), token);
        }
        catch (Exception ex)
        {
            LogBatchSetError(ex, cacheEntries);
            return false;
        }
    }

    private void LogBatchSetError<T>(Exception ex, CacheEntryValue<T>[] cacheEntries)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            LogInnerCacheSetKeysError(ex, Logged(cacheEntries.Select(o => o.CacheEntry).ToArray(), typeof(T)));
        }
    }

    private bool MemorySet<T>(CacheEntryValue<T>[] cacheEntries, TimeSpan? maxExpiration) =>
        Array.TrueForAll(cacheEntries, e => MemorySet(e.CacheEntry, e.Value, maxExpiration));

    private bool MemorySet<T>(CacheEntryOptions options, T value, TimeSpan? maxExpiration)
    {
        var item = _cacheEntryFactory.Create(value, options.Expiration);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "TryAdd for {CacheKey} reported not-added: the local lock could not be acquired within Lock.LocalLockTimeout, and without it two in-process callers could both be told they added the key. Raise Lock.LocalLockTimeout if this key is contended.")]
    private partial void LogTryAddLocalLockUnavailable(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {CacheKey}: the requested expiration {Expiration} is not in the future, so the entry would retain nothing.")]
    private partial void LogTryAddSkippedExpiredEntry(LoggedKey cacheKey, DateTimeOffset expiration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {CacheKey}: the effective local retention {LocalMaxExpiration} is not positive, and on this provider it is the only retention.")]
    private partial void LogTryAddSkippedNonPositiveLocalRetention(LoggedKey cacheKey, TimeSpan localMaxExpiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TryAdd won cacheKey {CacheKey} but the invalidation broadcast reported not-published. The add still stands; peers may serve a stale copy until it expires.")]
    private partial void LogTryAddBroadcastNotPublished(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replacing cached key {CacheKey}")]
    private partial void LogReplacingCachedKey(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Setting local only for cacheKey {CacheKey}")]
    private partial void LogSettingLocalOnly(LoggedKey cacheKey);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set value for {CacheKey}")]
    private partial void LogInnerCacheSetError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set value for {CacheKeys}")]
    private partial void LogInnerCacheSetKeysError(Exception ex, LoggedKeys cacheKeys);

    /// <summary>What the batch rehydrate write needs to know about one state.</summary>
    private readonly record struct RehydrateTarget(CacheEntryOptions Options, DateTimeOffset Expiration, CacheKey CallerKey);

    private readonly struct CacheEntryValue<T>(CacheEntryOptions cacheEntry, T? value)
    {
        public CacheEntryOptions CacheEntry { get; init; } = cacheEntry;
        public T? Value { get; init; } = value;
    }

    private sealed record BatchRun(int Index, IInFlightReservation<object?> Run);

    private sealed record BatchJoin<T>(int Index, InFlight<InFlightKey, object?>.Joined Join, Task<ICacheEntry<T?>> Run);
}
