using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache : MultilayerCacheBase, IHashCache, ISpanKeyHashCache
{
    // A hash that stays a write short for long, such as when a write of this node was never laid over it, must not hold every write since.
    private const int MaxOwnWritesAhead = 32;

    private readonly Lock[] _mergeLocks = Enumerable.Range(0, 64).Select(static _ => new Lock()).ToArray();
    private readonly IHashCache _innerCache;
    private readonly InFlight<InFlightKey, object?> _generations = new();
    private readonly HashCacheEntryBuilder _entryBuilder;
    private readonly HashLocalMemorySetter _localMemorySetter;
    private readonly IVersionedHashCache? _versioned;
    private readonly PeerFieldLimits _peerLimits;

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
        _entryBuilder = new HashCacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _localMemorySetter = new HashLocalMemorySetter(cacheName, changeTokenFactory, _topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions, memoryCacheOptions, telemetryProvider, _masker, !_clearLocalOnReconnect)
        {
            Invalidations = innerCache as IInvalidationSink,
        };
        if (_multiLayerCacheOptions is InMemoryRedisCacheOptions { PeerFieldUpdates: true } peer && innerCache is IVersionedHashCache versioned && _topicProvider is not NullTopicProvider)
        {
            _versioned = versioned;
            _background.Activate();
            _peerLimits = new PeerFieldLimits(Math.Max(1, peer.PeerFieldUpdateMaxFields), Math.Max(1, peer.PeerFieldUpdateMaxBytes));
            _localMemorySetter.PeerApply = TryApplyPeerUpdate;
        }
    }

    /// <summary>Runs and inner reads still in the tables; tests check they drain once callers are done.</summary>
    /// <summary>The tier told of received invalidations: the inner cache when it tracks them; tests check the wiring.</summary>
    internal IInvalidationSink? Invalidations => _localMemorySetter.Invalidations;

    internal int RunsInFlight => _innerReads.Count + _generations.Count + _background.InFlight;

    private bool OnlyTier => _innerCache is NullHashCache;

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

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(Span<char> cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        token.ThrowIfCancellationRequested();
        if (TryGetLocal<T>(cacheKey, out var local))
        {
            return new ValueTask<IDictionary<string, T?>>(local.Value is null ? Empty<T>() : SelectFields(local.Value, fields));
        }

        return GetAsync<T>(new CacheKey(cacheKey), fields, policy, token);
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
        // One floored resolution for the write and the rehydrate threshold; the miss path draws the jittered write deadline, so a hit never reads the clock.
        return GetOrAddInternalAsync(cacheKey, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, HashCacheSetOption.KeyReplace, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
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
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, setOption ?? HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddResolvedAsync(new CacheKey(cacheKey), generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, setOption ?? HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, state, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, HashCacheSetOption.KeyReplace, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, setOption ?? HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddResolvedAsync(new CacheKey(cacheKey), state, generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, setOption ?? HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
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

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), values, policy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), values, expiration, policy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), values, expiration, policy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), values, options, policy, token);

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        // An empty write replaces the key whole in the inner tier, so it does locally too.
        var setOption = IsNullOrEmpty(values) ? HashCacheSetOption.KeyReplace : options.SetOption;
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, GetExpiration(options, policy), setOption, token) with { Partial = setOption == HashCacheSetOption.HashReplace };
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
            return await Layered<T>().InternalSetAsync(cacheEntryOptions, values, innerCacheDisconnected, policy).ConfigureAwait(false);
        }

        if (policy.BackgroundDistributedWrite == true && _background.TryReserve())
        {
            return SetInBackground(cacheEntryOptions, values, policy);
        }

        var (written, kept, counted) = await Layered<T>().SetInnerAsync(cacheEntryOptions, values, innerCacheDisconnected, policy, keepRefused: false).ConfigureAwait(false);
        return written && await Layered<T>().PublishSetAsync(cacheEntryOptions, counted).ConfigureAwait(false) && kept;
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token));
    }

    public async ValueTask<bool> RemoveFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateFieldsForWrite(fields);
        if (_innerCache is not ISpanKeyHashCache inner)
        {
            throw new NotSupportedException($"{_innerCache.GetType().Name} does not remove single fields of a hash.");
        }

        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        if (fields.Length == 0)
        {
            return true;
        }

        // The whole local hash goes, here and on every node: an entry is replaced whole, never patched, so a read racing the removal cannot bring a removed field back.
        LogClearingLocalCached(Logged(options, typeof(T)));
        var removed = false;
        var issued = false;
        var cancelled = false;
        try
        {
            // The callback reports that the delete went out, so a cancel while waiting for an earlier write is told apart from one during it.
            (issued, removed, cancelled) = await _background.WriteAsync(
                options.CacheKey.Name,
                (Cache: this, Options: options, Fields: fields, Inner: inner),
                static s => s.Cache.RemoveLocal(s.Options.CacheKey.Name),
                [AsyncMethodBuilder(typeof(Pooled<>))] static async ValueTask<(bool Issued, bool Removed, bool Cancelled)> (s) =>
                {
                    try
                    {
                        var removed = s.Cache._versioned is { } versioned
                            ? await versioned.RemoveFieldsVersionedAsync<T>(s.Options.CacheKey, s.Fields, s.Options.Token).ConfigureAwait(false)
                            : await s.Inner.RemoveFieldsAsync<T>(s.Options.CacheKey, s.Fields, s.Options.Token).ConfigureAwait(false);
                        return (true, removed, false);
                    }
                    catch (OperationCanceledException) when (s.Options.Token.IsCancellationRequested)
                    {
                        return (true, false, true);
                    }
                    catch (Exception ex)
                    {
                        s.Cache.LogInnerCacheRemoveError(ex, s.Cache.Logged(s.Options, typeof(T)));
                        return (true, false, false);
                    }
                },
                options.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !options.Token.IsCancellationRequested)
        {
            LogInnerCacheRemoveError(ex, Logged(options, typeof(T)));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        if (!issued)
        {
            options.Token.ThrowIfCancellationRequested();
            return false;
        }

        // Announced however the delete ended, a cancel included: some of its chunks may have landed.
        bool announced;
        try
        {
            announced = await _eventPublisher.CacheRemovedAsync(options, typeof(T)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheRemoveError(ex, Logged(options, typeof(T)));
            announced = false;
        }

        if (cancelled)
        {
            throw new OperationCanceledException(options.Token);
        }

        return announced && removed;
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

        LogRefreshingInnerCacheKey(Logged(cacheEntryOptions, typeof(T)), cacheEntryOptions.Expiration);
        try
        {
            return await _background.WriteAsync(
                cacheEntryOptions.CacheKey.Name,
                (Cache: this, Options: cacheEntryOptions, Refresh: options, Expiration: expiration, Policy: policy, Token: token),
                static s => s.Cache.RemoveLocal(s.Options.CacheKey.Name),
                static async s =>
                {
                    var fired = await s.Cache._eventPublisher.CacheRefreshedAsync(s.Options, typeof(T)).ConfigureAwait(false);
                    // Forward the multilayer-resolved expiration so the inner write uses the same TTL the broadcast announced.
                    var innerOptions = s.Refresh with { ExpireTime = s.Expiration, TimeToLive = null };
                    return fired && await s.Cache._innerCache.RefreshAsync<T>(s.Options.CacheKey, innerOptions, s.Policy, s.Token).ConfigureAwait(false);
                },
                token).ConfigureAwait(false);
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

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local)
            ? new ValueTask<TimeSpan?>(local.Expiration.Subtract(_clock.GetUtcNow()))
            : TimeToLiveAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local)
            ? new ValueTask<DateTimeOffset?>(local.Expiration)
            : ExpireTimeAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(options.CacheKey.Name, out var entry)
            ? (entry?.Metadata)
            : await _innerCache.GetMetadataAsync<T>(options.CacheKey, token).ConfigureAwait(false);
    }

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local)
            ? new ValueTask<IDictionary<string, string?>?>(local.Metadata)
            : GetMetadataAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, token);
        LogSetMetadata(Logged(cacheEntryOptions, typeof(T)));
        try
        {
            // Released before the event; the local removal of a failure stays inside it.
            var updated = await _background.WriteAsync(
                cacheEntryOptions.CacheKey.Name,
                (Cache: this, Options: cacheEntryOptions, Metadata: metadata),
                null,
                static async s =>
                {
                    try
                    {
                        if (!await s.Cache._innerCache.SetMetadataAsync<T>(s.Options.CacheKey, s.Metadata, s.Options.Token).ConfigureAwait(false))
                        {
                            s.Cache.LogInnerCacheSetMetadataFailed(s.Cache.Logged(s.Options, typeof(T)));
                            return false;
                        }

                        return true;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && s.Options.Token.IsCancellationRequested))
                    {
                        s.Cache.RemoveLocal(s.Options.CacheKey.Name);
                        s.Cache.LogInnerCacheRefreshError(ex, s.Cache.Logged(s.Options, typeof(T)));
                        return false;
                    }
                },
                cacheEntryOptions.Token).ConfigureAwait(false);
            if (!updated)
            {
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
        catch (Exception ex) when (!(ex is OperationCanceledException && cacheEntryOptions.Token.IsCancellationRequested))
        {
            RemoveLocal(cacheEntryOptions.CacheKey.Name);
            LogInnerCacheRefreshError(ex, Logged(cacheEntryOptions, typeof(T)));
            return false;
        }
    }

    /// <summary>The held entry takes a write's fields only when it was exactly one write behind it: then the merge is the hash the write left, with this node's own later writes still over it.</summary>
    internal bool ApplyPeerUpdate<T>(RefreshMetadataState state, ICacheEntry<IDictionary<string, T?>> held, HashVersion version, PeerUpdate update)
    {
        if (_versioned is not { } versioned || !held.Found || held.Value is null || !version.TryResolve(out var number, out var ahead) || number <= 0)
        {
            return false;
        }

        // A write of the epoch the copy stands on, which the copy already has: an event can come late, behind a newer one or behind a read.
        if (update.Version <= number && HashVersion.SameEpoch(number, update.Version))
        {
            return true;
        }

        if (number != update.Version - 1 || HashVersion.ReturnedAtOrBefore(ahead, update.Version))
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        var expiration = update.Expiration ?? held.Expiration;
        TimeSpan? remaining = null;
        if (expiration <= now || (version.LocalDeadline is { } deadline && (remaining = deadline - now) <= TimeSpan.Zero))
        {
            return false;
        }

        if (!versioned.TryDecodeFields<T>(update.Fields, out var written))
        {
            return false;
        }

        var changes = new List<KeyValuePair<string, T?>>(written.Count);
        foreach (var (field, value) in written)
        {
            if (!HashVersion.Writes(ahead, field))
            {
                changes.Add(new(field, value));
            }
        }

        var combined = held.Value.ToImmutableDictionary().SetItems(changes);

        var options = new InternalHashCacheEntryOptions
        {
            CacheKey = state.CacheKey,
            CallerKey = state.CallerKey,
            TopicKey = state.TopicKey,
            Expiration = expiration,
            Metadata = update.Metadata ?? held.Metadata,
        };
        var merged = CreateEntry(combined, options);
        HashVersions.Stamp<T>(merged, update.Version, version.LocalDeadline, ahead);
        return _localMemorySetter.Set(options, merged, typeof(T), remaining);
    }

    private static bool IsNullOrEmpty<T>(IDictionary<string, T?>? value) =>
        value is null || value.Count == 0;

    private static ImmutableDictionary<string, T?> Empty<T>() =>
        ImmutableDictionary<string, T?>.Empty;

    private ValueTask<IDictionary<string, T?>> GetOrAddResolvedAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, long seen, bool localMissed, CancellationToken token)
    {
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, HashCacheSetOption.KeyReplace, policy, seen, localMissed, token);
    }

    private ValueTask<IDictionary<string, T?>> GetOrAddResolvedAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, long seen, bool localMissed, CancellationToken token)
    {
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, state, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, HashCacheSetOption.KeyReplace, policy, seen, localMissed, token);
    }

    private ValueTask<IDictionary<string, T?>> GetOrAddInternalAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, HashCacheSetOption setOption, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // Binds the state to a delegate only where one is needed, so a hit builds no closure.
        ArgumentNullException.ThrowIfNull(generator);
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !token.IsCancellationRequested)
        {
            var probe = ProbeLocal<T>(cacheKey, out var local);
            if (probe == LocalProbe.Hit && local!.Found)
            {
                var layered = Layered<T>();
                if (layered.ShouldRehydrate(local.Value, policy, effectiveDuration))
                {
                    layered.TriggerRehydrate(cacheKey, local.Expiration, StatefulCacheExtensions.BindHash(state, generator), policy, effectiveDuration, rehydrateJitter);
                }

                return new ValueTask<IDictionary<string, T?>>(local.Value ?? Empty<T>());
            }

            localMissed = probe == LocalProbe.Miss;
        }

        return GetOrAddCoreAsync(cacheKey, StatefulCacheExtensions.BindHash(state, generator), expiration, effectiveDuration, rehydrateJitter, setOption, policy, seen, localMissed, token);
    }

    /// <param name="expiration">The caller's write deadline, or null for a policy write, whose jittered deadline the miss path draws.</param>
    /// <param name="seen">The local writes counter, read before the caller's first read of the local tier.</param>
    /// <param name="localMissed">True when that read found nothing under the key, so the miss path need not read it again.</param>
    private ValueTask<IDictionary<string, T?>> GetOrAddInternalAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, HashCacheSetOption setOption, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // Not async: the miss path's lock delegates capture these parameters, and an async method would build that closure on a hit too.
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !token.IsCancellationRequested)
        {
            var probe = ProbeLocal<T>(cacheKey, out var local);
            if (probe == LocalProbe.Hit && local!.Found)
            {
                Layered<T>().TryRehydrate(cacheKey, local.Expiration, local.Value, generator, policy, effectiveDuration, rehydrateJitter);
                return new ValueTask<IDictionary<string, T?>>(local.Value ?? Empty<T>());
            }

            localMissed = probe == LocalProbe.Miss;
        }

        return GetOrAddCoreAsync(cacheKey, generator, expiration, effectiveDuration, rehydrateJitter, setOption, policy, seen, localMissed, token);
    }

    private async ValueTask<IDictionary<string, T?>> GetOrAddCoreAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, HashCacheSetOption setOption, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration ?? _clock.ToDateTimeOffset(ApplyJitter(effectiveDuration, rehydrateJitter)), setOption, token);
        var cacheEntry = await GetCacheEntryAsync<T>(cacheEntryOptions, policy, localMissed).ConfigureAwait(false);
        if (cacheEntry.Found)
        {
            Layered<T>().TryRehydrate(cacheKey, cacheEntry.Expiration, cacheEntry.Value, generator, policy, effectiveDuration, rehydrateJitter);
            return cacheEntry.Value ?? Empty<T>();
        }

        var result = SharesRuns(policy.Lock)
            ? await RunFirstAsync(cacheEntryOptions, generator, policy, seen, token).ConfigureAwait(false)
            : (ICacheEntry<IDictionary<string, T?>>)(await GenerateGuardedAsync<T>(cacheEntryOptions, generator, policy, new GenerationScope(null, token)).ConfigureAwait(false))!;
        return result.Value ?? Empty<T>();
    }

    /// <summary>The generation itself: under the key's distributed lock when one is enabled, which the caller that runs it takes, holds until the run ends, and follows with a read of both tiers that may find what another process stored; otherwise the generator at once.</summary>
    private ValueTask<object?> GenerateGuardedAsync<T>(InternalHashCacheEntryOptions options, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, GenerationScope scope) =>
        DistributedLockOn(policy.Lock)
            ? GenerateUnderDistributedLockAsync(options, generator, policy, scope)
            : RunHashGeneratorAndStoreEntryAsync(options, generator, policy, scope);

    private async ValueTask<object?> GenerateUnderDistributedLockAsync<T>(InternalHashCacheEntryOptions options, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, GenerationScope scope)
    {
        await using var lease = await AcquireDistributedLockAsync(options.CacheKey, policy.Lock, scope.Token).ConfigureAwait(false);
        // Read on the run's token: the starter may have left while callers that joined still wait for this.
        var current = await GetCacheEntryAsync<T>(options with { Token = scope.Token }, policy, false).ConfigureAwait(false);
        return current.Found ? current : await RunHashGeneratorAndStoreEntryAsync(options, generator, policy, scope).ConfigureAwait(false);
    }

    /// <summary>Takes the key's run before anything else and runs the generator inline in the caller that took it: a caller that arrives meanwhile joins the run at once; only the caller that runs it takes the distributed lock, if one is enabled.</summary>
    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> RunFirstAsync<T>(InternalHashCacheEntryOptions options, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, long seen, CancellationToken token) =>
        (ICacheEntry<IDictionary<string, T?>>)(await _generations.RunAsync(
            new InFlightKey(options.CacheKey.Name, typeof(T), null, null),
            (Cache: this, Options: options, Generator: generator, Policy: policy, Seen: seen),
            static (state, run) =>
                // A write in this process since the caller read the key may have left its value in the local tier, which a run writes before it leaves the table.
                state.Cache._localMemorySetter.Writes != state.Seen && state.Cache.TryGetLocalEntry<T>(state.Options, out var current)
                    ? new ValueTask<object?>(current)
                    : state.Cache.GenerateGuardedAsync<T>(state.Options, state.Generator, state.Policy, new GenerationScope(run, run.Token)),
            token).ConfigureAwait(false))!;

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
            // The starter's token, unless it cancelled while callers that joined still wait for the value: then the run's, which outlives it.
            var store = cacheEntryOptions.Token.IsCancellationRequested && !scope.IsAbandoned ? cacheEntryOptions with { Token = scope.Token } : cacheEntryOptions;
            // A peer that takes the distributed lock next must find the value.
            if (!innerCacheDisconnected && policy.BackgroundDistributedWrite == true && !DistributedLockOn(policy.Lock) && _background.TryReserve())
            {
                FillInBackground(store, ret ?? Empty<T>(), policy);
            }
            else
            {
                await Layered<T>().InternalSetAsync(store, ret ?? Empty<T>(), innerCacheDisconnected, policy, keepRefused: true).ConfigureAwait(false);
            }
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
            return await Layered<T>().InternalSetAsync(options, values, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
        else
        {
            if (policy.BackgroundDistributedWrite == true && _background.TryReserve())
            {
                return SetInBackground(options, values, policy);
            }

            var (written, kept, counted) = await Layered<T>().SetInnerAsync(options, values, innerCacheDisconnected, policy, keepRefused: false).ConfigureAwait(false);
            return written && await Layered<T>().PublishSetAsync(options, counted).ConfigureAwait(false) && kept;
        }
    }

    private async ValueTask<bool> RemoveAsync<T>(InternalHashCacheEntryOptions options)
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

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(InternalHashCacheEntryOptions options, CachePolicy policy, bool localMissed = false)
    {
        if (!localMissed && _memoryCache.TryGetValue<ICacheEntry<IDictionary<string, T?>>>(options.CacheKey.Name, out var cacheEntry))
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
                RemoveLocal(options.CacheKey.Name);
                LogReturningDefaultDisconnected(Logged(options, typeof(T)));
                return _cacheEntryFactory.Create<IDictionary<string, T?>>(Empty<T>(), default, default);
            }
        }

        var fetched = await Layered<T>().FetchInnerAsync(options, policy).ConfigureAwait(false);
        if (!fetched.Found)
        {
            return fetched;
        }

        options.Expiration = fetched.Expiration;
        options.Metadata = fetched.Metadata;
        return Filter(fetched, options);
    }

    private bool MemorySet<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, TimeSpan? maxExpiration, bool keepWhenAbsent = false)
    {
        if (!options.Partial)
        {
            return MemorySet(options, CreateEntry(value, options), maxExpiration);
        }

        // Read, merged and written as one, so two merges of one hash do not each drop the other's fields.
        lock (MergeLock(options.CacheKey.Name))
        {
            if (TryMergeLocal(ref options, value, out var merged))
            {
                return MemorySet(options, CreateEntry(merged, options), maxExpiration);
            }

            // Nothing held here to merge into: the fields alone are not the hash, so the next read takes it whole from the inner tier, unless there is none.
            options.Version = 0;
            return !(keepWhenAbsent || OnlyTier) || MemorySet(options, CreateEntry(value, options), maxExpiration);
        }
    }

    // Under the key's merge lock, so a merge never reads the hash a write is replacing and writes it back.
    private bool MemorySet<T>(InternalHashCacheEntryOptions options, ICacheEntry<IDictionary<string, T?>> item, TimeSpan? maxExpiration)
    {
        lock (MergeLock(options.CacheKey.Name))
        {
            if (_versioned is not null && (options.Version > 0 || options.Own is { Length: > 0 }))
            {
                HashVersions.Stamp<T>(item, options.Version, maxExpiration is { } max ? _clock.ToDateTimeOffset(max) : null, options.Own);
            }

            return _localMemorySetter.Set(options, item, typeof(T), maxExpiration);
        }
    }

    private bool TryApplyPeerUpdate(RefreshMetadataState state, PeerUpdate update)
    {
        var name = state.CacheKey.Name;
        try
        {
            // The same lock a local merge takes, so the two take their turns over the held hash.
            lock (MergeLock(name))
            {
                if (_memoryCache.TryGetValue(name, out object? current) && current is not null
                    && HashVersions.TryGet(current, out var version) && version.Apply(this, state, current, update))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogPeerUpdateFailed(ex, Logged(state.CallerKey, state.EntryType));
        }

        // What is held may lack the write, whether or not it is the entry the event was for.
        RemoveLocal(name);
        return false;
    }

    /// <summary>The copy a later background write built was made before this write landed, so it lacks its fields and its count.</summary>
    private bool DropSuperseded(string name)
    {
        if (_versioned is null)
        {
            return true;
        }

        // Not under the merge lock: removing an entry disposes its change token, which waits on the lock a delivery holds while it waits on this one.
        _memoryCache.Remove(name);
        return true;
    }

    private Lock MergeLock(string name) => _mergeLocks[(int)((uint)StringComparer.Ordinal.GetHashCode(name) % (uint)_mergeLocks.Length)];

    /// <summary>False when the local tier holds no hash to merge over, or holds one newer than the write.</summary>
    private bool TryMergeLocal<T>(ref InternalHashCacheEntryOptions options, IDictionary<string, T?> fields, [MaybeNullWhen(false)] out IDictionary<string, T?> merged)
    {
        if (!_memoryCache.TryGetValue(options.CacheKey.Name, out ICacheEntry<IDictionary<string, T?>>? held) || held is null || !held.Found || held.Value is null)
        {
            merged = null;
            options.Version = 0;
            options.Own = null;
            return false;
        }

        // The merge stands on what the held hash stands on, with the write laid over it; a hash that stands on nothing known leaves the entry without a version.
        var write = options.Pending ?? (options.Version > 0 ? new OwnWrite([.. fields.Keys]) { Version = options.Version } : null);
        options.Pending = null;
        options.Version = 0;
        options.Own = null;
        if (_versioned is not null && write is not null && HashVersions.TryGet(held, out var stamp) && stamp.TryResolve(out var number, out var ahead))
        {
            // The held hash already stands on this write or a later one: its fields are newer than the write's.
            if (write.Version > 0 && write.Version <= number && HashVersion.SameEpoch(number, write.Version))
            {
                merged = null;
                return false;
            }

            if (ahead.Length < MaxOwnWritesAhead)
            {
                options.Version = number;
                options.Own = [.. ahead, write];
            }
        }

        merged = held.Value.ToImmutableDictionary().SetItems(fields);
        options.Metadata ??= held.Metadata;
        return true;
    }

    private ICacheEntry<IDictionary<string, T?>> Filter<T>(ICacheEntry<IDictionary<string, T?>> cacheEntry, InternalHashCacheEntryOptions options)
    {
        if (options.Fields == null || cacheEntry.Value == null)
        {
            return cacheEntry;
        }
        return CreateEntry(SelectFields(cacheEntry.Value, options.Fields), options);
    }

    private ICacheEntry<IDictionary<string, T?>> CreateEntry<T>(IDictionary<string, T?> values, InternalHashCacheEntryOptions options) =>
        _cacheEntryFactory.Create<IDictionary<string, T?>>(values.ToImmutableDictionary(), options.Expiration, options.Metadata?.ToImmutableDictionary());

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replacing cached cacheKey {CacheKey}")]
    private partial void LogReplacingCachedKey(LoggedKey cacheKey);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "A peer update to {CacheKey} could not be applied; the local copy is dropped.")]
    private partial void LogPeerUpdateFailed(Exception ex, LoggedKey cacheKey);

}
