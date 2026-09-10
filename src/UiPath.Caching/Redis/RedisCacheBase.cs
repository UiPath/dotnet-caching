using System.Runtime.CompilerServices;
using UiPath.Caching.Config;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

public abstract class RedisCacheBase : IConnectionState, IDisposable
{
    private readonly IRedisConnector _redis;
    private readonly IConnectionState _connectionState;
    private readonly KeyMasker _masker;
    private bool _disposed;

    protected RedisCacheBase(
        IRedisConnector redis,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _masker = KeyMasker.For(keyMaskingPolicy, KnownCacheProviderNames.Redis);
        _redis = redis;
        Telemetry = telemetryProvider;
        var monitorConnection = redisCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled;
        _connectionState = monitorConnection ? redis : NullConnectionStateMonitor.Instance;
        DefaultPolicy = CachePolicyMerger.Merge(
            new CachePolicy { DistributedExpiration = redisCacheOptions.DefaultExpiration },
            policyFactory.Default);
        DefaultExpiration = DefaultPolicy.DistributedExpiration;
        Clock = clock;
        KeyReadTelemetryEnabled = redisCacheOptions.KeyReadTelemetryEnabled;
        RefreshFlags = redisCacheOptions.AwaitRefresh
            ? CommandFlags.DemandMaster
            : CommandFlags.DemandMaster | CommandFlags.FireAndForget;
    }

    public event EventHandler? OnConnectionFailed
    {
        add => _connectionState.OnConnectionFailed += value;
        remove => _connectionState.OnConnectionFailed -= value;
    }

    public event EventHandler? OnConnectionRestored
    {
        add => _connectionState.OnConnectionRestored += value;
        remove => _connectionState.OnConnectionRestored -= value;
    }

    public event EventHandler? OnReconnected
    {
        add => _connectionState.OnReconnected += value;
        remove => _connectionState.OnReconnected -= value;
    }

    public bool IsConnected => _connectionState.IsConnected;

    /// <summary>Flags for a standalone TTL write, shared by both caches so the option cannot be honored in one and not the other.</summary>
    internal CommandFlags RefreshFlags { get; }

    protected ICachingTelemetryProvider Telemetry { get; }

    protected bool KeyReadTelemetryEnabled { get; }

    protected CachePolicy DefaultPolicy { get; }

    protected TimeSpan? DefaultExpiration { get; }

    protected TimeProvider Clock { get; }

    protected IDatabase Database => _redis.Database;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Validates a caller-supplied duration.</summary>
    protected static TimeSpan CallerDuration(TimeSpan expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        CacheExpiration.ThrowIfNotPositive(expiration, paramName);

    protected void TrackRead(ITelemetryOperation operation, bool hit, RedisKey key)
    {
        operation.Track(hit, 1);
        if (KeyReadTelemetryEnabled)
        {
            operation.TrackKeyReads([(key.ToString(), hit)]);
        }
    }

    /// <summary>Write duration when the call carries none: policy, then cache default, then <see cref="CachePolicy.DefaultDistributedExpiration"/>; never unbounded by omission.</summary>
    protected TimeSpan PolicyDuration(CachePolicy? policy) =>
        policy?.DistributedExpiration ?? DefaultExpiration ?? CachePolicy.DefaultDistributedExpiration;

    /// <summary>Validates a caller-supplied expiration and turns it into a duration from the cache's now.</summary>
    protected TimeSpan CallerDuration(DateTimeOffset expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        CacheExpiration.ToDuration(expiration, Clock.GetUtcNow(), paramName);

    /// <summary><see cref="PolicyDuration"/> as an expiration.</summary>
    protected DateTimeOffset GetExpiration(CachePolicy? policy) =>
        Clock.ToDateTimeOffset(PolicyDuration(policy));

    /// <summary>Write expiration from an options object: <c>ExpireTime</c>, then <c>TimeToLive</c>, then <see cref="PolicyDuration"/>.</summary>
    protected DateTimeOffset GetExpiration(HashCacheEntryOptions options, CachePolicy? policy) =>
        options.ExpireTime ?? Clock.ToDateTimeOffset(options.TimeToLive ?? PolicyDuration(policy));

    /// <summary>Validates a caller-supplied duration and turns it into an expiration from the cache's now.</summary>
    protected DateTimeOffset GetExpiration(TimeSpan expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        Clock.ToDateTimeOffset(CallerDuration(expiration, paramName));

    /// <summary>Validates a caller-supplied expiration.</summary>
    protected DateTimeOffset GetExpiration(DateTimeOffset expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        CacheExpiration.ThrowIfNotFuture(expiration, Clock.GetUtcNow(), paramName);

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Dispose managed resources
            }
            _disposed = true;
        }
    }

    /// <summary>The key as a log line should show it. Nothing is rendered unless the line is written.</summary>
    private protected LoggedKey Logged(CacheKey key, RedisKey composed, Type? valueType = null) =>
        LoggedKey.For(_masker, key, composed, valueType);

    /// <inheritdoc cref="Logged(CacheKey, RedisKey, Type?)"/>
    private protected LoggedKey Logged(CacheKey key, Type? valueType = null) => LoggedKey.For(_masker, key, valueType);

    /// <summary>For the sites that only hold the composed key; it is judged, and masked, whole.</summary>
    private protected LoggedKey Logged(RedisKey composed, Type? valueType = null) =>
        LoggedKey.Composed(_masker, composed, valueType);

    /// <summary>
    /// Redis answers a cross-slot command with an error the caches log and report as a miss, so a batch
    /// spanning slots would read as a cache that never hits. Call it inside the command's own try, since
    /// reading a slot resolves the connection, and let <see cref="CrossSlotKeysException"/> back out past
    /// the catch that turns a Redis failure into a miss.
    /// </summary>
    private protected void ThrowIfCrossSlot(CacheKey[] cacheKeys, RedisKey[] redisKeys, Type? valueType, [CallerMemberName] string? operation = null)
    {
        if (redisKeys.Length < 2)
        {
            return;
        }

        var multiplexer = Database.Multiplexer;
        var slot = multiplexer.GetHashSlot(redisKeys[0]);
        for (var i = 1; i < redisKeys.Length; i++)
        {
            if (multiplexer.GetHashSlot(redisKeys[i]) == slot)
            {
                continue;
            }
            throw new CrossSlotKeysException(
                $"{operation} was given {redisKeys.Length} keys that Redis Cluster maps to different slots, " +
                $"starting with '{Logged(cacheKeys[0], redisKeys[0], valueType)}' and '{Logged(cacheKeys[i], redisKeys[i], valueType)}'. " +
                "A multi-key command runs on one node: give the keys a shared hash tag (non-empty content " +
                "between '{' and '}', e.g. 'app:s:{org1}:groups_1') so they hash together, or split the batch " +
                "into one call per group of keys that already share a tag.");
        }
    }
}
