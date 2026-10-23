using System.Runtime.CompilerServices;
using UiPath.Caching.Config;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

public abstract class RedisCacheBase : IConnectionState, IDisposable
{
    private readonly IRedisConnector _redis;
    private readonly IConnectionState _connectionState;
    private readonly KeyMasker _masker;
    private readonly string _keyPrefix;
    private readonly ConnectorKeyPrefix _connectorPrefix;
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
        _keyPrefix = redisCacheOptions.KeyPrefix ?? string.Empty;
        _connectorPrefix = ConnectorKeyPrefix.For(redis);
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

    private protected static Task<TResult[]> PerSlotAsync<TState, TResult>(int[][] groups, TState state, Func<TState, int[], ValueTask<TResult>> run, TResult failed, Action<Exception> logFailure) =>
        SlotFanOut.RunAsync(groups, state, run, failed, logFailure);

    /// <summary>The items at <paramref name="positions"/>; the array itself when they are all of it.</summary>
    private protected static TItem[] Pick<TItem>(TItem[] items, int[] positions) =>
        positions.Length == items.Length ? items : Array.ConvertAll(positions, i => items[i]);

    /// <summary>The key as a log line should show it. Nothing is rendered unless the line is written.</summary>
    private protected LoggedKey Logged(CacheKey key, RedisKey composed, Type? valueType = null) =>
        LoggedKey.For(_masker, key, composed, valueType);

    /// <inheritdoc cref="Logged(CacheKey, RedisKey, Type?)"/>
    private protected LoggedKey Logged(CacheKey key, Type? valueType = null) => LoggedKey.For(_masker, key, valueType);

    /// <summary>For the sites that only hold the composed key; it is judged, and masked, whole.</summary>
    private protected LoggedKey Logged(RedisKey composed, Type? valueType = null) =>
        LoggedKey.Composed(_masker, composed, valueType);

    /// <summary>The positions of <paramref name="redisKeys"/> by Redis Cluster slot; inside the command's try, since it resolves the connection.</summary>
    private protected int[][] SlotGroups(RedisKey[] redisKeys, ILogger logger)
    {
        if (redisKeys.Length < 2)
        {
            return [Enumerable.Range(0, redisKeys.Length).ToArray()];
        }

        var database = Database;
        var multiplexer = database.Multiplexer;
        // Outside the pipeline's latency cap, so this never waits on the probe.
        var prefix = _connectorPrefix.Get(database, _keyPrefix, logger);
        var first = multiplexer.GetHashSlot(WithConnectorPrefix(prefix, redisKeys[0]));
        Dictionary<int, List<int>>? groups = null;
        for (var i = 1; i < redisKeys.Length; i++)
        {
            var slot = multiplexer.GetHashSlot(WithConnectorPrefix(prefix, redisKeys[i]));
            if (groups is null)
            {
                if (slot == first)
                {
                    continue;
                }

                groups = new() { [first] = [.. Enumerable.Range(0, i)] };
            }

            if (!groups.TryGetValue(slot, out var group))
            {
                groups[slot] = group = [];
            }

            group.Add(i);
        }

        return groups is null ? [Enumerable.Range(0, redisKeys.Length).ToArray()] : [.. groups.Values.Select(g => g.ToArray())];
    }

    /// <summary>The key as the server hashes it, with the prefix the connector's <see cref="IDatabase"/> applies through <c>WithKeyPrefix</c>.</summary>
    private static RedisKey WithConnectorPrefix(string prefix, RedisKey key) => prefix.Length == 0 ? key : key.Prepend(prefix);
}
