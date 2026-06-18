using System.Collections.Immutable;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache : MultilayerCacheBase, IHashCache
{
    private readonly IHashCache _innerCache;
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
        ILogger logger)
        : base(cacheName, innerCache, memoryCacheFactory, topicFactory, cacheEventFactory, telemetryProvider, multiLayerCacheOptions, memoryCacheOptions, cacheOptions, localLock, distributedLock, policyFactory, logger)
    {
        _innerCache = innerCache;
        var cacheKeyStrategy = _multiLayerCacheOptions.CacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        var topicKeyStrategy = _multiLayerCacheOptions.TopicKeyStrategy ?? new DefaultTopicKeyStrategy(cacheOptions.Separator);
        _entryBuilder = new HashCacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _localMemorySetter = new HashLocalMemorySetter(cacheName, changeTokenFactory, _topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions, memoryCacheOptions, telemetryProvider);
    }

    public async ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, new[] { field }, default, token: token), policy);
        if (cacheEntry.Value == null)
        {
            return default;
        }

        return cacheEntry.Value.TryGetValue(field, out var value) ? value : default;
    }

    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, token), policy);
        return cacheEntry.Value ?? Empty<T>();
    }

    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, fields, default, token: token), policy);
        return cacheEntry?.Value ?? Empty<T>();
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        return GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, token), policy);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var duration = policy.DistributedExpiration;
        var writeExpiration = _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var duration = expiration ?? policy.DistributedExpiration;
        return GetOrAddInternalAsync(cacheKey, generator, _clock.ToDateTimeOffset(ResolveWriteDuration(policy, expiration)), duration, HashCacheSetOption.KeyReplace, policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        TimeSpan? duration;
        if (expiration.HasValue)
        {
            duration = expiration.Value - _clock.UtcNow;
            if (duration is { } d && d <= TimeSpan.Zero) { duration = null; }
        }
        else
        {
            duration = policy.DistributedExpiration;
            expiration = _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        }
        return GetOrAddInternalAsync(cacheKey, generator, expiration, duration, HashCacheSetOption.KeyReplace, policy, token);
    }

    /// <summary>
    /// Never returns <c>null</c>. A cache hit (real data or cached-empty marker) returns the stored
    /// dictionary or an empty one; a cache miss invokes the generator and returns its result. The inner
    /// cache may legally return <c>Found=true</c> with <c>Value=null</c> when only the
    /// <c>_metadata_</c>-as-empty-marker is present; we collapse that to <see cref="Empty{T}"/> for the
    /// caller, who can always iterate the result without a null check.
    /// </summary>
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, HashCacheSetOption? setOption = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        TimeSpan? duration;
        if (expiration.HasValue)
        {
            duration = expiration.Value - _clock.UtcNow;
            if (duration is { } d && d <= TimeSpan.Zero) { duration = null; }
        }
        else
        {
            duration = policy.DistributedExpiration;
            expiration = _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        }
        return GetOrAddInternalAsync(cacheKey, generator, expiration, duration, setOption ?? HashCacheSetOption.KeyReplace, policy, token);
    }

    private async ValueTask<IDictionary<string, T?>> GetOrAddInternalAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, TimeSpan? effectiveDuration, HashCacheSetOption setOption, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, setOption, token);
        var cacheEntry = await GetCacheEntryAsync<T>(cacheEntryOptions, policy).ConfigureAwait(false);
        if (cacheEntry.Found)
        {
            TryHashRehydrate(cacheKey, cacheEntry.Expiration, cacheEntry.Value, generator, policy, effectiveDuration);
            return cacheEntry.Value ?? Empty<T>();
        }

        var result = await RunUnderLocksAsync<ICacheEntry<IDictionary<string, T?>>>(
            cacheEntryOptions.CacheKey,
            () => GetCacheEntryAsync<T>(cacheEntryOptions, policy),
            e => e.Found,
            ct => RunHashGeneratorAndStoreEntryAsync(cacheEntryOptions, generator, policy, ct),
            token,
            policyLock: policy.Lock).ConfigureAwait(false);
        return result.Value ?? Empty<T>();
    }

    private void TryHashRehydrate<T>(CacheKey originalCacheKey, DateTimeOffset entryExpiration, IDictionary<string, T?>? currentValue, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, TimeSpan? effectiveDuration)
    {
        if (policy.RehydrateEnabled != true || policy.Rehydrate is null)
        {
            return;
        }
        if (IsNullOrEmpty(currentValue) && _multiLayerCacheOptions.CacheNullValues)
        {
            return;
        }
        var resolvedDuration = effectiveDuration ?? policy.DistributedExpiration ?? _multiLayerCacheOptions.DefaultExpiration;
        if (resolvedDuration is not { } duration || duration <= TimeSpan.Zero)
        {
            return;
        }
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
                    : _clock.UtcNow.Add(duration);
                var rehydrateOptions = _entryBuilder.BuildEntryOptions<T>(originalCacheKey, rehydrateExpiration, HashCacheSetOption.KeyReplace, ct);
                var innerCacheDisconnected = GetInnerCacheDisconnected();
                var fired = innerCacheDisconnected || await _eventPublisher.CacheSetAsync(rehydrateOptions).ConfigureAwait(false);
                var written = fired && await InternalSetAsync(rehydrateOptions, newValue ?? Empty<T>(), innerCacheDisconnected, policy).ConfigureAwait(false);
                if (!written)
                {
                    throw new RehydrateWriteFailedException(originalCacheKey.Name);
                }
            });
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> RunHashGeneratorAndStoreEntryAsync<T>(InternalHashCacheEntryOptions cacheEntryOptions, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, CancellationToken token)
    {
        LogCacheMissed(cacheEntryOptions.CacheKey);
        var ret = await InvokeFactoryAsync(cacheEntryOptions.CacheKey, generator, policy.FactoryTimeout, token).ConfigureAwait(false);

        if (!IsNullOrEmpty(ret) || _multiLayerCacheOptions.CacheNullValues)
        {
            var innerCacheDisconnected = GetInnerCacheDisconnected();
            await InternalSetAsync(cacheEntryOptions, ret ?? Empty<T>(), innerCacheDisconnected, policy).ConfigureAwait(false);
        }
        return _cacheEntryFactory.Create<IDictionary<string, T?>>(ret ?? Empty<T>(), cacheEntryOptions.Expiration, cacheEntryOptions.Metadata);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetAsync(cacheKey, values, ResolveWriteDuration(policy), policy, token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetAsync(cacheKey, values, _clock.ToDateTimeOffset(ResolveWriteDuration(policy, expiration)), policy, token);
    }

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        expiration ??= _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token: token);
        if (IsNullOrEmpty(values) && !_multiLayerCacheOptions.CacheNullValues)
        {
            return await RemoveAsync<T>(options).ConfigureAwait(false);
        }

        values ??= new Dictionary<string, T?>();

        LogReplacingCachedKey(options.CacheKey);
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            LogSettingLocalOnly(options.CacheKey);
            return await InternalSetAsync(options, values, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
        else
        {
            var fired = await _eventPublisher.CacheSetAsync(options).ConfigureAwait(false);
            return fired && await InternalSetAsync(options, values, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        DateTimeOffset? expiration;
        if (options.ExpireTime.HasValue)
        {
            expiration = _clock.ToDateTimeOffset(options.ExpireTime);
        }
        else if (options.TimeToLive.HasValue)
        {
            expiration = _clock.ToDateTimeOffset(options.TimeToLive);
        }
        else
        {
            expiration = _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        }
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, options.SetOption, token);
        cacheEntryOptions.Metadata = options.Metadata;
        if (IsNullOrEmpty(values) && !_multiLayerCacheOptions.CacheNullValues)
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        values ??= new Dictionary<string, T?>();

        LogReplacingCachedKey(cacheEntryOptions.CacheKey);
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            LogSettingLocalOnly(cacheEntryOptions.CacheKey);
            return await InternalSetAsync(cacheEntryOptions, values, innerCacheDisconnected, policy).ConfigureAwait(false);
        }

        var fired = await _eventPublisher.CacheSetAsync(cacheEntryOptions).ConfigureAwait(false);
        return fired && await InternalSetAsync(cacheEntryOptions, values, innerCacheDisconnected, policy).ConfigureAwait(false);
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token));
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return RefreshAsync<T>(cacheKey, ResolveWriteDuration(policy), policy, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return RefreshAsync<T>(cacheKey, _clock.ToDateTimeOffset(ResolveWriteDuration(policy, expiration)), policy, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, new HashCacheEntryOptions(expiration), policy, token);

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var resolvedTtl = options.TimeToLive
            ?? (options.ExpireTime.HasValue ? null : ResolveWriteDuration(policy));
        var expiration = options.ExpireTime.HasValue
            ? _clock.ToDateTimeOffset(options.ExpireTime)
            : _clock.ToDateTimeOffset(resolvedTtl);
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token: token);
        cacheEntryOptions.Metadata = options.Metadata;
        LogClearingCached(cacheEntryOptions.CacheKey);

        _memoryCache.Remove(cacheEntryOptions.CacheKey);
        LogRefreshingInnerCacheKey(cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration);
        try
        {
            var fired = await _eventPublisher.CacheRefreshedAsync(cacheEntryOptions).ConfigureAwait(false);
            // Forward the multilayer-resolved expiration so the inner write uses the same TTL the broadcast announced.
            var innerOptions = options with { ExpireTime = expiration, TimeToLive = null };
            return fired && await _innerCache.RefreshAsync<T>(cacheEntryOptions.CacheKey, innerOptions, policy, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheRefreshError(ex, cacheEntryOptions.CacheKey);
            return false;
        }
    }

    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        try
        {
            return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey, out _) || await _innerCache.ContainsAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheContainsError(ex, cacheEntryOptions.CacheKey);
            return false;
        }
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey, out var value)
            ? value?.Expiration.Subtract(_clock.UtcNow)
            : await _innerCache.TimeToLiveAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey, out var value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(options.CacheKey, out var entry)
            ? (entry?.Metadata)
            : await _innerCache.GetMetadataAsync<T>(options.CacheKey, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, token);
        LogSetMetadata(cacheEntryOptions.CacheKey);
        try
        {
            var response = await _innerCache.SetMetadataAsync<T>(cacheEntryOptions.CacheKey, metadata, cacheEntryOptions.Token).ConfigureAwait(false);
            if (!response)
            {
                LogInnerCacheSetMetadataFailed(cacheEntryOptions.CacheKey);
                return false;
            }

            if(_memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey, out var entry) && entry != null)
            {
                cacheEntryOptions.Expiration = entry.Expiration;
            }
            else
            {
                var expiration = await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
                cacheEntryOptions.Expiration = _clock.ToDateTimeOffset(expiration);
            }

            cacheEntryOptions.Metadata = metadata;
            return await _eventPublisher.MetadataUpdatedAsync(cacheEntryOptions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _memoryCache.Remove(cacheEntryOptions.CacheKey);
            LogInnerCacheRefreshError(ex, cacheEntryOptions.CacheKey);
            return false;
        }
    }

    private async ValueTask<bool> RemoveAsync<T>(InternalHashCacheEntryOptions options)
    {
        LogClearingLocalCached(options.CacheKey);
        try
        {
            _memoryCache.Remove(options.CacheKey);
            var removed = await _innerCache.RemoveAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);
            var eventFired = await _eventPublisher.CacheRemovedAsync(options).ConfigureAwait(false);
            return removed && eventFired;
        }
        catch (Exception ex)
        {
            LogInnerCacheRemoveError(ex, options.CacheKey);
            return false;
        }
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(InternalHashCacheEntryOptions options, CachePolicy policy)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<IDictionary<string, T?>>>(options.CacheKey, out var cacheEntry))
        {
            LogFoundLocal(options.CacheKey);
            if (_connectionState.IsConnected)
            {
                return Filter(cacheEntry!, options);
            }
            else if (_useLocalOnlyWhenDisconnected)
            {
                LogUsingLocalCopyDisconnected(options.CacheKey);
                return Filter(cacheEntry!, options);
            }
            else
            {
                _memoryCache.Remove(options.CacheKey);
                LogReturningDefaultDisconnected(options.CacheKey);
                return _cacheEntryFactory.Create<IDictionary<string, T?>>(Empty<T>(), default, default);
            }
        }

        cacheEntry = await _innerCache.GetCacheEntryAsync<T>(options.CacheKey, policy, options.Token).ConfigureAwait(false);

        if (!cacheEntry.Found)
        {
            return cacheEntry!;
        }

        LogFoundInnerCopy(options.CacheKey);
        options.Expiration = cacheEntry.Expiration;
        options.Metadata = cacheEntry.Metadata;
        var values = cacheEntry.Value ?? Empty<T>();
        MemorySet(options, values, policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration);

        return Filter(cacheEntry!, options);
    }

    private bool MemorySet<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, TimeSpan? maxExpiration)
    {
        var item = CreateEntry(value, options);
        return _localMemorySetter.Set(options, item, typeof(T), maxExpiration);
    }

    private async ValueTask<bool> InternalSetAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, bool disconnected, CachePolicy policy)
    {
        try
        {
            if (disconnected)
            {
                LogSettingLocalOnly(options.CacheKey);
                return MemorySet(options, value, policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected);
            }

            var ret = await _innerCache.SetAsync<T?>(options.CacheKey, value, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), policy, options.Token).ConfigureAwait(false);
            return ret && MemorySet(options, value, policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration);
        }
        catch (Exception ex)
        {
            LogInnerCacheSetError(ex, options.CacheKey);
            return false;
        }
    }

    private ICacheEntry<IDictionary<string, T?>> Filter<T>(ICacheEntry<IDictionary<string, T?>> cacheEntry, InternalHashCacheEntryOptions options)
    {
        if (options.Fields == null || cacheEntry.Value == null)
        {
            return cacheEntry;
        }
        var allFields = options.Fields.ToHashSet(StringComparer.InvariantCultureIgnoreCase);
        var values = cacheEntry.Value.Where(kv => allFields.Contains(kv.Key)).ToImmutableDictionary(kv => kv.Key, kv => kv.Value);
        return CreateEntry(values, options);
    }

    private ICacheEntry<IDictionary<string, T?>> CreateEntry<T>(IDictionary<string, T?> values, InternalHashCacheEntryOptions options) =>
        _cacheEntryFactory.Create<IDictionary<string, T?>>(values.ToImmutableDictionary(), options.Expiration, options.Metadata?.ToImmutableDictionary());

    private static bool IsNullOrEmpty<T>(IDictionary<string, T?>? value) =>
        value is null || value.Count == 0;

    private static ImmutableDictionary<string, T?> Empty<T>() =>
        ImmutableDictionary<string, T?>.Empty;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replacing cached cacheKey {CacheKey}")]
    private partial void LogReplacingCachedKey(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Setting local only for cacheKey {CacheKey}")]
    private partial void LogSettingLocalOnly(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing cached. cacheKey {CacheKey}")]
    private partial void LogClearingCached(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing inner cache cacheKey {CacheKey} at expiration {Expiration}")]
    private partial void LogRefreshingInnerCacheKey(CacheKey cacheKey, DateTimeOffset? expiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache refresh value for cacheKey {CacheKey}")]
    private partial void LogInnerCacheRefreshError(Exception ex, CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache contains for cacheKey {CacheKey}")]
    private partial void LogInnerCacheContainsError(Exception ex, CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Set metadata for cacheKey {CacheKey}")]
    private partial void LogSetMetadata(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set metadata for cacheKey {CacheKey} failed")]
    private partial void LogInnerCacheSetMetadataFailed(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing local cached. cacheKey {CacheKey}")]
    private partial void LogClearingLocalCached(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache remove cacheKey {CacheKey}")]
    private partial void LogInnerCacheRemoveError(Exception ex, CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found local. {CacheKey}")]
    private partial void LogFoundLocal(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Using local copy for cacheKey {CacheKey}")]
    private partial void LogUsingLocalCopyDisconnected(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Returning default for cacheKey {CacheKey}")]
    private partial void LogReturningDefaultDisconnected(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found inner copy at cacheKey {CacheKey}")]
    private partial void LogFoundInnerCopy(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set value for {CacheKey}")]
    private partial void LogInnerCacheSetError(Exception ex, CacheKey cacheKey);
}
