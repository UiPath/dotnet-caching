using System.Collections.Immutable;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache : MultilayerCacheBase, IHashCache, ISpanKeyHashCache
{
    private readonly IHashCache _innerCache;
    private readonly InFlight<InFlightKey, object?> _innerReads = new();
    private readonly InFlight<InFlightKey, object?> _generations = new();
    private readonly HashCacheEntryBuilder _entryBuilder;
    private readonly HashLocalMemorySetter _localMemorySetter;

    public MultilayerHashCache(
        string cacheName,
        IHashCache innerCache,
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
        _entryBuilder = new HashCacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _localMemorySetter = new HashLocalMemorySetter(cacheName, changeTokenFactory, _topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions, memoryCacheOptions, telemetryProvider, _masker, !_clearLocalOnReconnect);
    }

    [OverloadResolutionPriority(1)]
    public async ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local) && TryGetItem(local, field, out var item))
        {
            return item;
        }

        policy ??= _defaultPolicy;
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, new[] { field }, default, token: token), policy);
        if (cacheEntry.Value == null)
        {
            return default;
        }

        return cacheEntry.Value.TryGetValue(field, out var value) ? value : default;
    }

    public ValueTask<T?> GetItemAsync<T>(Span<char> cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        // The hash key path observes cancellation first, before the key and the strategy.
        token.ThrowIfCancellationRequested();
        if (TryGetLocal<T>(cacheKey, out var local) && TryGetItem(local, field, out var item))
        {
            return new ValueTask<T?>(item);
        }

        return GetItemAsync<T>(new CacheKey(cacheKey), field, policy, token);
    }

    [OverloadResolutionPriority(1)]
    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local))
        {
            return local.Value ?? Empty<T>();
        }

        policy ??= _defaultPolicy;
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, token), policy);
        return cacheEntry.Value ?? Empty<T>();
    }

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        token.ThrowIfCancellationRequested();
        if (TryGetLocal<T>(cacheKey, out var local))
        {
            return new ValueTask<IDictionary<string, T?>>(local.Value ?? Empty<T>());
        }

        return GetAsync<T>(new CacheKey(cacheKey), policy, token);
    }

    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, fields, default, token: token), policy);
        return cacheEntry?.Value ?? Empty<T>();
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local))
        {
            return new ValueTask<ICacheEntry<IDictionary<string, T?>>>(local);
        }

        policy ??= _defaultPolicy;
        return GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, token), policy);
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        token.ThrowIfCancellationRequested();
        if (TryGetLocal<T>(cacheKey, out var local))
        {
            return new ValueTask<ICacheEntry<IDictionary<string, T?>>>(local);
        }

        return GetCacheEntryAsync<T>(new CacheKey(cacheKey), policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        // One floored resolution for the write and the rehydrate threshold; only the write is jittered.
        var duration = ResolveDuration(policy);
        var writeExpiration = _clock.ToDateTimeOffset(ApplyJitter(duration, policy.JitterMaxDuration));
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, policy.JitterMaxDuration, HashCacheSetOption.KeyReplace, policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, token);
    }

    /// <summary>
    /// Never returns <c>null</c>. A cache hit (real data or cached-empty marker) returns the stored
    /// dictionary or an empty one; a cache miss invokes the generator and returns its result. The inner
    /// cache may legally return <c>Found=true</c> with <c>Value=null</c> when only the
    /// <c>_metadata_</c>-as-empty-marker is present; we collapse that to <see cref="Empty{T}"/> for the
    /// caller, who can always iterate the result without a null check.
    /// </summary>
    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, setOption ?? HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return TryGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values)
            ? new ValueTask<IDictionary<string, T?>>(values)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return TryGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values)
            ? new ValueTask<IDictionary<string, T?>>(values)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return TryGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values)
            ? new ValueTask<IDictionary<string, T?>>(values)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return TryGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values)
            ? new ValueTask<IDictionary<string, T?>>(values)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, setOption ?? HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetCoreAsync(cacheKey, values, GetExpiration(policy), policy, token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, GetExpiration(options, policy), options.SetOption, token);
        cacheEntryOptions.Metadata = options.Metadata;
        if (IsNullOrEmpty(values) && !_multiLayerCacheOptions.CacheNullValues)
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        values ??= new Dictionary<string, T?>();

        LogReplacingCachedKey(Logged(cacheEntryOptions, typeof(T)));
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            LogSettingLocalOnly(Logged(cacheEntryOptions, typeof(T)));
            return await InternalSetAsync(cacheEntryOptions, values, innerCacheDisconnected, policy).ConfigureAwait(false);
        }

        var fired = await _eventPublisher.CacheSetAsync(cacheEntryOptions, typeof(T)).ConfigureAwait(false);
        return fired && await InternalSetAsync(cacheEntryOptions, values, innerCacheDisconnected, policy).ConfigureAwait(false);
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token));
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, new HashCacheEntryOptions(), policy, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, new HashCacheEntryOptions(GetExpiration(expiration)), policy, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, new HashCacheEntryOptions(GetExpiration(expiration)), policy, token);

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var expiration = GetExpiration(options, policy);
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token: token);
        cacheEntryOptions.Metadata = options.Metadata;
        LogClearingCached(Logged(cacheEntryOptions, typeof(T)));

        _memoryCache.Remove(cacheEntryOptions.CacheKey.Name);
        LogRefreshingInnerCacheKey(Logged(cacheEntryOptions, typeof(T)), cacheEntryOptions.Expiration);
        try
        {
            var fired = await _eventPublisher.CacheRefreshedAsync(cacheEntryOptions, typeof(T)).ConfigureAwait(false);
            // Forward the multilayer-resolved expiration so the inner write uses the same TTL the broadcast announced.
            var innerOptions = options with { ExpireTime = expiration, TimeToLive = null };
            return fired && await _innerCache.RefreshAsync<T>(cacheEntryOptions.CacheKey, innerOptions, policy, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheRefreshError(ex, Logged(cacheEntryOptions, typeof(T)));
            return false;
        }
    }

    [OverloadResolutionPriority(1)]
    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out _))
        {
            return true;
        }

        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        try
        {
            return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey.Name, out _) || await _innerCache.ContainsAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheContainsError(ex, Logged(cacheEntryOptions, typeof(T)));
            return false;
        }
    }

    public ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out _)
            ? new ValueTask<bool>(true)
            : ContainsAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var value)
            ? value?.Expiration.Subtract(_clock.GetUtcNow())
            : await _innerCache.TimeToLiveAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(options.CacheKey.Name, out var entry)
            ? (entry?.Metadata)
            : await _innerCache.GetMetadataAsync<T>(options.CacheKey, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, token);
        LogSetMetadata(Logged(cacheEntryOptions, typeof(T)));
        try
        {
            var response = await _innerCache.SetMetadataAsync<T>(cacheEntryOptions.CacheKey, metadata, cacheEntryOptions.Token).ConfigureAwait(false);
            if (!response)
            {
                LogInnerCacheSetMetadataFailed(Logged(cacheEntryOptions, typeof(T)));
                return false;
            }

            if(_memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var entry) && entry != null)
            {
                cacheEntryOptions.Expiration = entry.Expiration;
            }
            else
            {
                var expiration = await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
                cacheEntryOptions.Expiration = _clock.ToDateTimeOffset(expiration);
            }

            cacheEntryOptions.Metadata = metadata;
            return await _eventPublisher.MetadataUpdatedAsync(cacheEntryOptions, typeof(T)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _memoryCache.Remove(cacheEntryOptions.CacheKey.Name);
            LogInnerCacheRefreshError(ex, Logged(cacheEntryOptions, typeof(T)));
            return false;
        }
    }

    private static bool IsNullOrEmpty<T>(IDictionary<string, T?>? value) =>
        value is null || value.Count == 0;

    private static ImmutableDictionary<string, T?> Empty<T>() =>
        ImmutableDictionary<string, T?>.Empty;

    private ValueTask<IDictionary<string, T?>> GetOrAddInternalAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, HashCacheSetOption setOption, CachePolicy policy, CancellationToken token)
    {
        // Not async: the miss path's lock delegates capture these parameters, and an async method would build that closure on a hit too.
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local) && local.Found)
        {
            TryHashRehydrate(cacheKey, local.Expiration, local.Value, generator, policy, effectiveDuration, rehydrateJitter);
            return new ValueTask<IDictionary<string, T?>>(local.Value ?? Empty<T>());
        }

        return GetOrAddCoreAsync(cacheKey, generator, expiration, effectiveDuration, rehydrateJitter, setOption, policy, token);
    }

    private async ValueTask<IDictionary<string, T?>> GetOrAddCoreAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, HashCacheSetOption setOption, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, setOption, token);
        var cacheEntry = await GetCacheEntryAsync<T>(cacheEntryOptions, policy).ConfigureAwait(false);
        if (cacheEntry.Found)
        {
            TryHashRehydrate(cacheKey, cacheEntry.Expiration, cacheEntry.Value, generator, policy, effectiveDuration, rehydrateJitter);
            return cacheEntry.Value ?? Empty<T>();
        }

        var lifetime = new LockLifetime();
        var result = await RunUnderLocksAsync(
            cacheEntryOptions.CacheKey,
            (Cache: this, Options: cacheEntryOptions, Generator: generator, Policy: policy, Lifetime: lifetime),
            static s => s.Cache.GetCacheEntryAsync<T>(s.Options, s.Policy),
            static e => e.Found,
            static (s, ct) => s.Cache.RunSharedAsync(s.Options, s.Generator, s.Policy, s.Lifetime, ct),
            token,
            policy.Lock,
            lifetime).ConfigureAwait(false);
        return result.Value ?? Empty<T>();
    }

    private void TryHashRehydrate<T>(CacheKey originalCacheKey, DateTimeOffset entryExpiration, IDictionary<string, T?>? currentValue, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, TimeSpan duration, TimeSpan? rehydrateJitter)
    {
        if (policy.RehydrateEnabled != true || policy.Rehydrate is null)
        {
            return;
        }
        if (IsNullOrEmpty(currentValue) && _multiLayerCacheOptions.CacheNullValues)
        {
            return;
        }
        if (duration <= TimeSpan.Zero || duration == TimeSpan.MaxValue)
        {
            return;
        }
        TriggerHashRehydrate(originalCacheKey, entryExpiration, generator, policy, duration, rehydrateJitter);
    }

    /// <summary>Hands the rehydration to the coordinator; apart from <see cref="TryHashRehydrate{T}"/> so its early returns do not allocate the closure.</summary>
    private void TriggerHashRehydrate<T>(CacheKey originalCacheKey, DateTimeOffset entryExpiration, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, TimeSpan duration, TimeSpan? rehydrateJitter)
    {
        _rehydrator.TryTrigger(
            originalCacheKey,
            entryExpiration,
            policy,
            duration,
            kind: "hash",
            rehydrateAsync: async ct =>
            {
                var newValue = await generator(ct).ConfigureAwait(false);
                if (IsNullOrEmpty(newValue) && !_multiLayerCacheOptions.CacheNullValues)
                {
                    return;
                }
                // Factory transitions to empty: preserve the original deadline so the marker doesn't get a fresh TTL window.
                var rehydrateExpiration = IsNullOrEmpty(newValue)
                    ? entryExpiration
                    : _clock.ToDateTimeOffset(ApplyJitter(duration, rehydrateJitter));
                var rehydrateOptions = _entryBuilder.BuildEntryOptions<T>(originalCacheKey, rehydrateExpiration, HashCacheSetOption.KeyReplace, ct);
                var innerCacheDisconnected = GetInnerCacheDisconnected();
                var fired = innerCacheDisconnected || await _eventPublisher.CacheSetAsync(rehydrateOptions, typeof(T)).ConfigureAwait(false);
                var written = fired && await InternalSetAsync(rehydrateOptions, newValue ?? Empty<T>(), innerCacheDisconnected, policy).ConfigureAwait(false);
                if (!written)
                {
                    throw new RehydrateWriteFailedException(originalCacheKey.Name);
                }
            },
            entryType: typeof(T));
    }

    /// <summary>Runs the generation as one run per key: a caller whose wait for the local lock timed out, or that arrives while it runs, joins it rather than starting another. Only the generator runs on the shared token; the write keeps the starting caller's. The starting caller's locks stay held until the run ends, even if that caller stops waiting first.</summary>
    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> RunSharedAsync<T>(InternalHashCacheEntryOptions options, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, LockLifetime lifetime, CancellationToken token)
    {
        if (!ResolveLocalLock(policy.Lock).Enabled)
        {
            return (ICacheEntry<IDictionary<string, T?>>)(await RunHashGeneratorAndStoreEntryAsync(options, generator, policy, new GenerationScope(token, null)).ConfigureAwait(false))!;
        }

        return (ICacheEntry<IDictionary<string, T?>>)(await _generations.RunAsync(
            new InFlightKey(options.CacheKey.Name, typeof(T), null, null),
            (Cache: this, Options: options, Generator: generator, Policy: policy, Lifetime: lifetime),
            static (state, run) =>
            {
                state.Lifetime.Run = run;

                // A run that ended in this process since the caller read the key left its value in the local tier, which it writes before it leaves the table.
                return state.Cache.TryGetLocalEntry<T>(state.Options, out var current)
                    ? new ValueTask<object?>(current)
                    : state.Cache.RunHashGeneratorAndStoreEntryAsync(state.Options, state.Generator, state.Policy, new GenerationScope(run.Token, run));
            },
            token).ConfigureAwait(false))!;
    }

    /// <summary>A local hit only, never the inner tier: for a caller that has just taken a key, where the only thing that can have changed is a run of this process.</summary>
    private bool TryGetLocalEntry<T>(InternalHashCacheEntryOptions options, [MaybeNullWhen(false)] out ICacheEntry<IDictionary<string, T?>> entry)
    {
        if ((_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) && _memoryCache.TryGetValue(options.CacheKey.Name, out entry) && entry!.Found)
        {
            return true;
        }

        entry = default;
        return false;
    }

    private async ValueTask<object?> RunHashGeneratorAndStoreEntryAsync<T>(InternalHashCacheEntryOptions cacheEntryOptions, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, GenerationScope scope)
    {
        LogCacheMissed(Logged(cacheEntryOptions, typeof(T)));
        var ret = await InvokeFactoryAsync(cacheEntryOptions.CacheKey, generator, policy.FactoryTimeout, scope.Token).ConfigureAwait(false);

        // A generator that ignores its token can finish after every caller left and a newer run took the key: its value must not replace theirs.
        if ((!IsNullOrEmpty(ret) || _multiLayerCacheOptions.CacheNullValues) && !scope.IsAbandoned)
        {
            var innerCacheDisconnected = GetInnerCacheDisconnected();
            await InternalSetAsync(cacheEntryOptions, ret ?? Empty<T>(), innerCacheDisconnected, policy, keepRefused: true).ConfigureAwait(false);
        }
        return _cacheEntryFactory.Create<IDictionary<string, T?>>(ret ?? Empty<T>(), cacheEntryOptions.Expiration, cacheEntryOptions.Metadata);
    }

    private async ValueTask<bool> SetCoreAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token: token);
        if (IsNullOrEmpty(values) && !_multiLayerCacheOptions.CacheNullValues)
        {
            return await RemoveAsync<T>(options).ConfigureAwait(false);
        }

        values ??= new Dictionary<string, T?>();

        LogReplacingCachedKey(Logged(options, typeof(T)));
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            LogSettingLocalOnly(Logged(options, typeof(T)));
            return await InternalSetAsync(options, values, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
        else
        {
            var fired = await _eventPublisher.CacheSetAsync(options, typeof(T)).ConfigureAwait(false);
            return fired && await InternalSetAsync(options, values, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> RemoveAsync<T>(InternalHashCacheEntryOptions options)
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

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(InternalHashCacheEntryOptions options, CachePolicy policy)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<IDictionary<string, T?>>>(options.CacheKey.Name, out var cacheEntry))
        {
            LogFoundLocal(Logged(options, typeof(T)));
            if (_connectionState.IsConnected)
            {
                return Filter(cacheEntry!, options);
            }
            else if (_useLocalOnlyWhenDisconnected)
            {
                LogUsingLocalCopyDisconnected(Logged(options, typeof(T)));
                return Filter(cacheEntry!, options);
            }
            else
            {
                _memoryCache.Remove(options.CacheKey.Name);
                LogReturningDefaultDisconnected(Logged(options, typeof(T)));
                return _cacheEntryFactory.Create<IDictionary<string, T?>>(Empty<T>(), default, default);
            }
        }

        cacheEntry = await FetchInnerAsync<T>(options, policy).ConfigureAwait(false);
        if (!cacheEntry.Found)
        {
            return cacheEntry!;
        }

        options.Expiration = cacheEntry.Expiration;
        options.Metadata = cacheEntry.Metadata;
        return Filter(cacheEntry!, options);
    }

    /// <summary>Reads the whole entry from the inner tier and keeps a hit locally; concurrent reads of one key share one inner read, and each caller filters its own fields.</summary>
    private ValueTask<ICacheEntry<IDictionary<string, T?>>> FetchInnerAsync<T>(InternalHashCacheEntryOptions options, CachePolicy policy) =>
        _innerCache is NullHashCache
            ? _innerCache.GetCacheEntryAsync<T>(options.CacheKey, policy, options.Token)
            : FetchSharedInnerAsync<T>(options, policy);

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> FetchSharedInnerAsync<T>(InternalHashCacheEntryOptions options, CachePolicy policy) =>
        (ICacheEntry<IDictionary<string, T?>>)(await _innerReads.RunAsync(
            new InFlightKey(options.CacheKey.Name, typeof(T), policy.LocalExpiration, policy.LocalExpirationDisconnected),
            (Cache: this, Options: options, Policy: policy),
            static (state, run) => state.Cache.FetchAndKeepAsync<T>(state.Options.Token == run.Token ? state.Options : state.Options with { Token = run.Token }, state.Policy, run),
            options.Token).ConfigureAwait(false))!;

    private async ValueTask<object?> FetchAndKeepAsync<T>(InternalHashCacheEntryOptions options, CachePolicy policy, IInFlightRun run)
    {
        var fetched = await _innerCache.GetCacheEntryAsync<T>(options.CacheKey, policy, options.Token).ConfigureAwait(false);
        if (fetched.Found)
        {
            // Committed only while a caller still waits, so a read every caller left cannot overwrite a fresh one.
            run.TryCommit((Cache: this, Options: options, Policy: policy, Fetched: fetched), static s => s.Cache.Keep(s.Options, s.Policy, s.Fetched));
        }

        return fetched;
    }

    private void Keep<T>(InternalHashCacheEntryOptions options, CachePolicy policy, ICacheEntry<IDictionary<string, T?>> fetched)
    {
        LogFoundInnerCopy(Logged(options, typeof(T)));
        options.Expiration = fetched.Expiration;
        options.Metadata = fetched.Metadata;
        MemorySet(options, fetched.Value ?? Empty<T>(), LocalLifetime(policy));
    }

    private bool MemorySet<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, TimeSpan? maxExpiration)
    {
        var item = CreateEntry(value, options);
        return _localMemorySetter.Set(options, item, typeof(T), maxExpiration);
    }

    private async ValueTask<bool> InternalSetAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, bool disconnected, CachePolicy policy, bool keepRefused = false)
    {
        try
        {
            if (disconnected)
            {
                LogSettingLocalOnly(Logged(options, typeof(T)));
                return MemorySet(options, value, policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected);
            }

            bool ret;
            try
            {
                ret = await _innerCache.SetAsync<T?>(options.CacheKey, value, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), policy, options.Token).ConfigureAwait(false);
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

    private ICacheEntry<IDictionary<string, T?>> Filter<T>(ICacheEntry<IDictionary<string, T?>> cacheEntry, InternalHashCacheEntryOptions options)
    {
        if (options.Fields == null || cacheEntry.Value == null)
        {
            return cacheEntry;
        }
        var allFields = options.Fields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = ImmutableDictionary.CreateBuilder<string, T?>();
        foreach (var (field, value) in cacheEntry.Value)
        {
            if (allFields.Contains(field))
            {
                values.Add(field, value);
            }
        }
        return CreateEntry(values.ToImmutable(), options);
    }

    private ICacheEntry<IDictionary<string, T?>> CreateEntry<T>(IDictionary<string, T?> values, InternalHashCacheEntryOptions options) =>
        _cacheEntryFactory.Create<IDictionary<string, T?>>(values.ToImmutableDictionary(), options.Expiration, options.Metadata?.ToImmutableDictionary());

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replacing cached cacheKey {CacheKey}")]
    private partial void LogReplacingCachedKey(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Setting local only for cacheKey {CacheKey}")]
    private partial void LogSettingLocalOnly(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing cached. cacheKey {CacheKey}")]
    private partial void LogClearingCached(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing inner cache cacheKey {CacheKey} at expiration {Expiration}")]
    private partial void LogRefreshingInnerCacheKey(LoggedKey cacheKey, DateTimeOffset? expiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache refresh value for cacheKey {CacheKey}")]
    private partial void LogInnerCacheRefreshError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache contains for cacheKey {CacheKey}")]
    private partial void LogInnerCacheContainsError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Set metadata for cacheKey {CacheKey}")]
    private partial void LogSetMetadata(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set metadata for cacheKey {CacheKey} failed")]
    private partial void LogInnerCacheSetMetadataFailed(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing local cached. cacheKey {CacheKey}")]
    private partial void LogClearingLocalCached(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache remove cacheKey {CacheKey}")]
    private partial void LogInnerCacheRemoveError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found local. {CacheKey}")]
    private partial void LogFoundLocal(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Using local copy for cacheKey {CacheKey}")]
    private partial void LogUsingLocalCopyDisconnected(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Returning default for cacheKey {CacheKey}")]
    private partial void LogReturningDefaultDisconnected(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found inner copy at cacheKey {CacheKey}")]
    private partial void LogFoundInnerCopy(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set value for {CacheKey}")]
    private partial void LogInnerCacheSetError(Exception ex, LoggedKey cacheKey);
}
