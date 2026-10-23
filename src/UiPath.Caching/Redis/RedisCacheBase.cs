using System.Runtime.CompilerServices;
using System.Text;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

public abstract class RedisCacheBase : IConnectionState, IDisposable
{
    private readonly IRedisConnector _redis;
    private readonly IConnectionState _connectionState;
    private readonly KeyMasker _masker;
    private readonly string _keyPrefix;
    private readonly RedisKey _keyPrefixKey;
    private readonly ConnectorKeyPrefix _connectorPrefix;
    private bool? _supportsExpireTime;
    // Set once scripts turn out to be disabled or denied on this server; reads then use a transaction.
    private bool _noReadScript;
    // Set by the first script that succeeds; until then any failure of a script turns scripts off.
    private bool _readScriptWorked;
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
        _keyPrefixKey = _keyPrefix.Length == 0 ? default : Encoding.UTF8.GetBytes(_keyPrefix);
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
        _noReadScript = !redisCacheOptions.ReadScriptsEnabled;
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

    /// <summary>EXPIRETIME arrived in Redis 7; before it, an expiration is the TTL added to now. Read on first use, since resolving the server version connects.</summary>
    protected bool SupportsExpireTime => _supportsExpireTime ??= RedisUtils.SupportsExpireTime(_redis.Version);

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

    /// <summary>One command under <paramref name="pipeline"/>, handed <paramref name="state"/> so it needs no closure: timed by <paramref name="operation"/>, tracked as a hit by <paramref name="isHit"/>, and on failure logged through <paramref name="logFailure"/> and answered with <paramref name="fallback"/>.</summary>
    protected static async ValueTask<TResult> RunAsync<TState, TResult>(
        IResiliencePipeline pipeline,
        TelemetryScope operation,
        Func<TState, CancellationToken, ValueTask<TResult>> command,
        TState state,
        TResult fallback,
        Func<TResult, bool> isHit,
        Action<Exception> logFailure,
        CancellationToken token)
    {
        var ret = fallback;
        try
        {
            token.ThrowIfCancellationRequested();
            ret = await pipeline.ExecuteAsync(command, state, fallback, token).ConfigureAwait(false);
            operation.Stop();
        }
        catch (Exception ex)
        {
            operation.Stop();
            logFailure(ex);
        }
        finally
        {
            operation.Track(isHit(ret));
        }

        return ret;
    }

    protected void TrackRead(TelemetryScope operation, bool hit, RedisKey key)
    {
        operation.Track(hit, 1);
        if (KeyReadTelemetryEnabled && operation.IsEnabled)
        {
            operation.TrackKeyReads([(key.ToString(), hit)]);
        }
    }

    /// <summary>The key's TTL, or null when it has none, the key is invalid or the command failed.</summary>
    protected ValueTask<TimeSpan?> KeyTimeToLiveAsync(IResiliencePipeline pipeline, TelemetryScope operation, IRedisKeyStrategy keyStrategy, CacheKey cacheKey, Action<Exception> logFailure, CancellationToken token) =>
        RunAsync(pipeline, operation, static (s, token) => s.Self.Database.KeyTimeToLiveAsync(ToRedisKey(s.Strategy, s.Key, token), CommandFlags.PreferReplica).AsValueTask(), (Self: this, Strategy: keyStrategy, Key: cacheKey), (TimeSpan?)null, static timeToLive => timeToLive != null, logFailure, token);

    /// <summary>The key's expiration: EXPIRETIME on Redis 7, else the TTL added to now, under the same operation.</summary>
    protected async ValueTask<DateTimeOffset?> KeyExpireTimeAsync(IResiliencePipeline pipeline, TelemetryScope operation, IRedisKeyStrategy keyStrategy, CacheKey cacheKey, Action<Exception> logFailure, CancellationToken token)
    {
        if (!await SupportsExpireTimeAsync(token).ConfigureAwait(false))
        {
            var timeToLive = await KeyTimeToLiveAsync(pipeline, operation, keyStrategy, cacheKey, logFailure, token).ConfigureAwait(false);
            return timeToLive.HasValue ? Clock.ToDateTimeOffset(timeToLive.Value) : null;
        }

        return await RunAsync(pipeline, operation, static (s, token) => s.Self.Database.KeyExpireTimeAsync(ToRedisKey(s.Strategy, s.Key, token), CommandFlags.PreferReplica).AsValueTask(), (Self: this, Strategy: keyStrategy, Key: cacheKey), (DateTime?)null, static expireTime => expireTime.HasValue, logFailure, token).ConfigureAwait(false);
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

    /// <summary>Waits for the first connect without blocking a thread, before a step that must run ahead of the command, such as reading the keys' slots; free once connected.</summary>
    /// <remarks>The wait runs inside <paramref name="pipeline"/>, which connects first, so its timeout, retries and circuit bound it as they bound the command. A pipeline that gives up throws, and the caller handles that as a failed command.</remarks>
    private protected ValueTask EnsureConnectedAsync(IResiliencePipeline pipeline, CancellationToken token)
    {
        var connect = _redis.ConnectAsync(token);
        if (connect.IsCompletedSuccessfully)
        {
            return ValueTask.CompletedTask;
        }

        // The pipeline may give up before it awaits this wait, so its fault is observed here.
        var pending = connect.AsTask();
        _ = pending.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return WaitAsync(pipeline, pending, token);

        static async ValueTask WaitAsync(IResiliencePipeline pipeline, Task pending, CancellationToken token)
        {
            var connected = await pipeline.ExecuteAsync(
                static async (wait, _) =>
                {
                    await wait.ConfigureAwait(false);
                    return true;
                },
                pending,
                false,
                token).ConfigureAwait(false);
            if (!connected)
            {
                throw new TimeoutException("Redis did not connect within the resilience pipeline's bounds.");
            }
        }
    }

    /// <summary>Waits for the first connect without blocking, since reading the server version connects; a connect that fails is left to the command, which logs it.</summary>
    private protected async ValueTask<bool> SupportsExpireTimeAsync(CancellationToken token)
    {
        try
        {
            await _redis.ConnectAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // GetVersion falls back to the configured version.
        }

        return SupportsExpireTime;
    }

    /// <summary>
    /// Runs a read-only script when the server has PEXPIRETIME (Redis 7) and has not refused scripts; a MULTI/EXEC pair allocates about three times as much per read in StackExchange.Redis.
    /// Null tells the caller to read with a transaction: scripts are off, or the server just refused one, which turns them off for good.
    /// Until one script has succeeded, any failure counts as a refusal, so a server or proxy that cannot run them, whatever it answers, never turns reads into misses;
    /// after that, only a refusal (unknown or not permitted) does, and other errors propagate to the read pipeline.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private protected async ValueTask<RedisResult?> TryReadScriptAsync(string script, RedisKey[] keys, Action<Exception> logUnavailable, RedisValue[]? values = null)
    {
        if (!SupportsExpireTime || _noReadScript)
        {
            return null;
        }

        try
        {
            var reply = await Database.ScriptEvaluateReadOnlyAsync(script, keys, values, CommandFlags.PreferReplica).ConfigureAwait(false);
            _readScriptWorked = true;
            return reply;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (!_readScriptWorked || RedisUtils.IsUnsupportedCommand(ex) || RedisUtils.IsDenied(ex)))
        {
            _noReadScript = true;
            logUnavailable(ex);
            return null;
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
        var prefix = _connectorPrefix.GetKey(database, _keyPrefix, _keyPrefixKey, logger);
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

    /// <summary>Whether Redis Cluster would keep the two keys on one slot, with the connector's prefix on both.</summary>
    private protected bool SameSlot(RedisKey first, RedisKey second, ILogger logger)
    {
        var database = Database;
        var prefix = _connectorPrefix.GetKey(database, _keyPrefix, _keyPrefixKey, logger);
        var multiplexer = database.Multiplexer;
        return multiplexer.GetHashSlot(WithConnectorPrefix(prefix, first)) == multiplexer.GetHashSlot(WithConnectorPrefix(prefix, second));
    }

    /// <summary>The key as the server hashes it, with the prefix the connector's <see cref="IDatabase"/> applies through <c>WithKeyPrefix</c>.</summary>
    private static RedisKey WithConnectorPrefix(RedisKey prefix, RedisKey key) => key.Prepend(prefix);

    /// <summary>Runs inside the command, so a null key or a cancelled token is logged and answered like any other failed read.</summary>
    private static RedisKey ToRedisKey(IRedisKeyStrategy keyStrategy, CacheKey cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }

        return keyStrategy.GetRedisKey(cacheKey);
    }
}
