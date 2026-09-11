using System.Runtime.CompilerServices;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

public abstract class MultilayerCacheBase : IDisposable
{
    protected readonly ILogger _logger;
    protected readonly IMemoryCache _memoryCache;
    protected readonly ICacheEntryFactory _cacheEntryFactory;
    protected readonly IMultilayerCacheOptions _multiLayerCacheOptions;
    protected readonly IDisposable _monitor;
    protected readonly TimeProvider _clock;
    protected readonly CacheEventPublisher _eventPublisher;
    protected readonly IConnectionState _connectionState;
    protected readonly ITopicProvider _topicProvider;
    protected readonly bool _useLocalOnlyWhenDisconnected;
    private protected readonly RehydrationCoordinator _rehydrator;
    private protected readonly CachePolicy _defaultPolicy;
    private protected readonly KeyMasker _masker;

    // Hardcoded fallback values for the lock fields. Merged in as the lowest-priority policy
    // (after provider-specific + user DefaultCachePolicy) so every cache instance has a fully
    // resolved Lock — every field non-null — by the time validation runs.
    // 
    // DistributedExpiration is deliberately not floored here: the floor is a write-side rule
    // (ResolveWriteDuration), and leaving it out keeps "nothing configured" observable as null.
    private static readonly CachePolicy HardcodedDefaults = new()
    {
        Lock = new LockProfile
        {
            LocalLockEnabled = true,
            DistributedLockEnabled = false,
            LocalLockTimeout = TimeSpan.FromMilliseconds(500),
            DistributedLockTimeout = TimeSpan.FromMilliseconds(500),
            DistributedLockExpiry = TimeSpan.FromSeconds(5),
        },
    };
    private readonly ILocalLock _localLock;
    private readonly IDistributedLock _distributedLock;
    private readonly IDistributedLockKeyStrategy _lockKeyStrategy;
    private readonly string _localLockKeyPrefix;
    private readonly TimeSpan _distributedLockExpiry;
    private readonly TimeSpan _distributedLockTimeout;
    private readonly TimeSpan _localLockTimeout;
    private readonly bool _localLockEnabled;
    private readonly bool _distributedLockEnabled;
    private bool _disposed;

    protected MultilayerCacheBase(
        string cacheName,
        object innerCache,
        IMemoryCacheFactory memoryCacheFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IMultilayerCacheOptions multiLayerCacheOptions,
        IMemoryCacheOptions memoryOptions,
        CacheOptions cacheOptions,
        ILocalLock localLock,
        IDistributedLock distributedLock,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        ILogger logger,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _masker = KeyMasker.For(keyMaskingPolicy, cacheName);
        _logger = logger;
        _multiLayerCacheOptions = multiLayerCacheOptions;
        _defaultPolicy = CachePolicyMerger.Merge(
            CachePolicyMerger.Merge(CachePolicyFromMultilayerOptions.Build(multiLayerCacheOptions), policyFactory.Default),
            HardcodedDefaults);
        CachePolicyFactoryValidator.ValidateAgainstEffectiveDefault(policyFactory, cacheOptions.DistributedLockPollInterval, _defaultPolicy);
        _memoryCache = memoryCacheFactory.Get(memoryOptions);
        Telemetry = telemetryProvider;
        _cacheEntryFactory = _multiLayerCacheOptions.EntryFactory ?? new CacheEntryFactory();
        _monitor = _memoryCache.Monitor(multiLayerCacheOptions, Telemetry, GetType().Name);
        _clock = clock;
        _topicProvider = topicFactory.Get(_multiLayerCacheOptions.Topic);
        _eventPublisher = new CacheEventPublisher(cacheName, _topicProvider, cacheEventFactory, logger, _masker);
        var connectionMonitorEnabled = multiLayerCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled;
        _connectionState = connectionMonitorEnabled ? GetConnectionMonitor(innerCache, _topicProvider) : NullConnectionStateMonitor.Instance;
        _useLocalOnlyWhenDisconnected = (multiLayerCacheOptions.UseLocalOnlyWhenDisconnected ?? false) && connectionMonitorEnabled;
        _localLock = localLock;
        _distributedLock = distributedLock;
        _lockKeyStrategy = multiLayerCacheOptions.LockKeyStrategy ?? new DefaultDistributedLockKeyStrategy(cacheOptions.Separator);
        _localLockKeyPrefix = cacheName + cacheOptions.Separator;
        var defaultLock = _defaultPolicy.Lock!;
        _distributedLockExpiry = defaultLock.DistributedLockExpiry!.Value;
        _distributedLockTimeout = defaultLock.DistributedLockTimeout!.Value;
        _localLockTimeout = defaultLock.LocalLockTimeout!.Value;
        _localLockEnabled = defaultLock.LocalLockEnabled!.Value;
        _distributedLockEnabled = defaultLock.DistributedLockEnabled!.Value;
        Name = cacheName;
        _rehydrator = new RehydrationCoordinator(cacheName, _clock, distributedLock, _lockKeyStrategy, telemetryProvider, logger, _masker);
    }

    public string Name { get; }

    protected ICachingTelemetryProvider Telemetry { get; }
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected static TimeSpan ApplyJitter(TimeSpan duration, TimeSpan? maxJitter)
    {
        // TimeSpan.MaxValue means "no TTL"; jittering it would clamp it under the sentinel and back onto EXPIRE.
        if (duration <= TimeSpan.Zero || duration == TimeSpan.MaxValue || maxJitter is not { } max || max <= TimeSpan.Zero)
        {
            return duration;
        }
        // Bounded under the sentinel, so the sum can neither overflow nor land on it; the clock fits it into the DateTime range.
        var bonusTicks = Random.Shared.NextInt64(Math.Min(max.Ticks, TimeSpan.MaxValue.Ticks - duration.Ticks));
        return duration + new TimeSpan(bonusTicks);
    }

    protected async ValueTask<TResult> RunUnderLocksAsync<TResult>(
        CacheKey cacheKey,
        Func<ValueTask<TResult>> readCachedAsync,
        Func<TResult, bool> isHit,
        Func<CancellationToken, ValueTask<TResult>> runGeneratorAndStoreAsync,
        CancellationToken token,
        LockProfile? policyLock = null)
    {
        var (localLockEnabled, localLockTimeout) = ResolveLocalLock(policyLock);
        var distributedLockEnabled = policyLock?.DistributedLockEnabled ?? _distributedLockEnabled;
        // Per-call LockProfile bypasses options validators; mirror LockSettingsValidator's accepted ranges and fall back when out-of-range.
        var distributedLockTimeout = NonNegativeOrFallback(policyLock?.DistributedLockTimeout, _distributedLockTimeout);
        var distributedLockExpiry = PositiveOrFallback(policyLock?.DistributedLockExpiry, _distributedLockExpiry);

        IDisposable? localLock = null;
        IAsyncDisposable? distributedLock = null;
        try
        {
            if (localLockEnabled)
            {
                localLock = await TryAcquireLocalLockAsync(cacheKey, localLockTimeout, token).ConfigureAwait(false);
                var fromCache = await readCachedAsync().ConfigureAwait(false);
                if (isHit(fromCache))
                {
                    return fromCache;
                }
            }

            if (distributedLockEnabled)
            {
                var lockKey = _lockKeyStrategy.GetLockKey(cacheKey);
                distributedLock = await _distributedLock.AcquireAsync(lockKey, distributedLockExpiry, distributedLockTimeout, token).ConfigureAwait(false);
                var fromCache = await readCachedAsync().ConfigureAwait(false);
                if (isHit(fromCache))
                {
                    return fromCache;
                }
            }

            return await runGeneratorAndStoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (distributedLock is not null)
                {
                    await distributedLock.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                localLock?.Dispose();
            }
        }
    }

    protected Task<TResult> InvokeFactoryAsync<TResult>(
        CacheKey cacheKey,
        Func<CancellationToken, Task<TResult>> factory,
        TimeSpan? factoryTimeout,
        CancellationToken token) =>
        FactoryTimeout.RunAsync(factory, factoryTimeout, cacheKey, Name, Telemetry, token);

    /// <summary>The L2 write lifetime: a caller value as-is, else <see cref="ResolveDuration"/> jittered.</summary>
    protected TimeSpan ResolveWriteDuration(CachePolicy policy, TimeSpan? callerExpiration = null) =>
        callerExpiration ?? ApplyJitter(ResolveDuration(policy), policy.JitterMaxDuration);

    /// <summary>
    /// The configured L2 lifetime, floored and unjittered: policy → options default → <see cref="CachePolicy.DefaultDistributedExpiration"/>.
    /// The rehydrate threshold measures against this, so it cannot depend on a per-write draw.
    /// </summary>
    protected TimeSpan ResolveDuration(CachePolicy policy) =>
        policy.DistributedExpiration
        ?? _multiLayerCacheOptions.DefaultExpiration
        ?? CachePolicy.DefaultDistributedExpiration;

    protected bool GetInnerCacheDisconnected() => _useLocalOnlyWhenDisconnected && !_connectionState.IsConnected;

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _monitor.Dispose();
                _memoryCache.Dispose();
                if (_connectionState is IDisposable connectionState)
                {
                    connectionState.Dispose();
                }
            }
            _disposed = true;
        }
    }

    /// <summary>The key as a log line should show it. Nothing is rendered unless the line is written.</summary>
    private protected LoggedKey Logged(CacheKey key, Type? valueType = null) => LoggedKey.For(_masker, key, valueType);

    /// <summary>Shows the composed key but judges, and masks, the caller's own key inside it.</summary>
    private protected LoggedKey Logged(CacheEntryOptions options, Type? valueType = null) =>
        LoggedKey.For(_masker, options.CallerKey, options.CacheKey.Name, valueType);

    /// <inheritdoc cref="Logged(CacheEntryOptions, Type?)"/>
    private protected LoggedKeys Logged(IReadOnlyCollection<CacheEntryOptions> options, Type? valueType = null) => new(_masker, options, valueType);

    /// <inheritdoc cref="Logged(CacheEntryOptions, Type?)"/>
    private protected LoggedKey Logged(InternalHashCacheEntryOptions options, Type? valueType = null) =>
        LoggedKey.For(_masker, options.CallerKey, options.CacheKey.Name, valueType);

    /// <inheritdoc cref="Logged(CacheKey, Type?)"/>
    private protected LoggedKeys Logged(IReadOnlyCollection<CacheKey> keys, Type? valueType = null) => new(_masker, keys, valueType);

    /// <summary>A key the site only has in composed form; with no caller key to judge it is masked whole.</summary>
    private protected LoggedKey LoggedComposed(CacheKey composed, Type? valueType = null) =>
        LoggedKey.Composed(_masker, composed.Name, valueType);

    /// <inheritdoc cref="LoggedComposed(CacheKey, Type?)"/>
    private protected LoggedKeys LoggedComposed(IReadOnlyCollection<CacheKey> composed, Type? valueType = null) =>
        new(_masker, composed, valueType, composedOnly: true);

    /// <summary>
    /// Validates a caller-supplied duration and pairs it with the deadline it implies. The write path
    /// needs both: the deadline for the entry options, the duration for the L1 cap and the rehydrate
    /// trigger. Jitter is deliberately not applied — a caller-supplied lifetime is honored exactly.
    /// </summary>
    private protected (DateTimeOffset Expiration, TimeSpan Duration) CallerWrite(TimeSpan expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null)
    {
        var duration = CacheExpiration.ThrowIfNotPositive(expiration, paramName);
        return (_clock.ToDateTimeOffset(duration), duration);
    }

    /// <inheritdoc cref="CallerWrite(TimeSpan, string)"/>
    private protected (DateTimeOffset Expiration, TimeSpan Duration) CallerWrite(DateTimeOffset expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        (expiration, CacheExpiration.ToDuration(expiration, _clock.GetUtcNow(), paramName));

    /// <summary>Write expiration from the policy chain, jittered.</summary>
    private protected DateTimeOffset GetExpiration(CachePolicy policy) =>
        _clock.ToDateTimeOffset(ResolveWriteDuration(policy));

    /// <summary>Write expiration from an options object: <c>ExpireTime</c>, then <c>TimeToLive</c>, then the policy chain.</summary>
    private protected DateTimeOffset GetExpiration(HashCacheEntryOptions options, CachePolicy policy) =>
        options.ExpireTime ?? _clock.ToDateTimeOffset(options.TimeToLive ?? ResolveWriteDuration(policy));

    /// <summary>Write expiration for a caller-supplied duration, validated.</summary>
    private protected DateTimeOffset GetExpiration(TimeSpan expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        CallerWrite(expiration, paramName).Expiration;

    /// <summary>Write expiration for a caller-supplied instant, validated.</summary>
    private protected DateTimeOffset GetExpiration(DateTimeOffset expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        CacheExpiration.ThrowIfNotFuture(expiration, _clock.GetUtcNow(), paramName);

    /// <summary>
    /// The local lock alone, for callers that need it for correctness rather than de-duplication.
    /// Taken regardless of <c>Lock.LocalLockEnabled</c>, which only trades single-flight for
    /// throughput on <c>GetOrAddAsync</c>; <c>null</c> means the acquire timed out, and the caller
    /// must fail closed.
    /// </summary>
    private protected ValueTask<IDisposable?> AcquireLocalLockAsync(CacheKey cacheKey, LockProfile? policyLock, CancellationToken token) =>
        TryAcquireLocalLockAsync(cacheKey, ResolveLocalLock(policyLock).Timeout, token);

    private static TimeSpan PositiveOrFallback(TimeSpan? value, TimeSpan fallback) =>
        value is { } v && v > TimeSpan.Zero ? v : fallback;

    private static TimeSpan NonNegativeOrFallback(TimeSpan? value, TimeSpan fallback) =>
        value is { } v && v >= TimeSpan.Zero ? v : fallback;

    /// <summary>
    /// One place for the local-lock policy: a per-call <see cref="LockProfile"/> wins over the
    /// options. It bypasses the options validators, so the timeout falls back when out of range.
    /// </summary>
    private (bool Enabled, TimeSpan Timeout) ResolveLocalLock(LockProfile? policyLock) =>
        (policyLock?.LocalLockEnabled ?? _localLockEnabled,
         PositiveOrFallback(policyLock?.LocalLockTimeout, _localLockTimeout));

    private async ValueTask<IDisposable?> TryAcquireLocalLockAsync(CacheKey cacheKey, TimeSpan localLockTimeout, CancellationToken token)
    {
        var lockKey = _localLockKeyPrefix + cacheKey.Name;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        // CancelAfter throws on > Int32.MaxValue ms; clamp so misconfigured Lock.LocalLockTimeout degrades gracefully.
        var clampedTimeout = localLockTimeout.TotalMilliseconds > int.MaxValue
            ? TimeSpan.FromMilliseconds(int.MaxValue)
            : localLockTimeout;
        linkedCts.CancelAfter(clampedTimeout);
        try
        {
            return await _localLock.AcquireAsync(lockKey, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    private IConnectionState GetConnectionMonitor(params object[] connectionStates)
    {
        var lst = connectionStates.OfType<IConnectionState>().ToArray();
        return lst.Length == 0 ? NullConnectionStateMonitor.Instance : new ConnectionStateMonitor(Telemetry, _multiLayerCacheOptions.ConnectionMonitorPeriod ?? TimeSpan.FromSeconds(5), lst);
    }

}
