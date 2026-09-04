using System.Runtime.CompilerServices;
using UiPath.Caching.Config;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

public abstract class RedisCacheBase : IConnectionState, IDisposable
{
    private readonly IRedisConnector _redis;
    private readonly IConnectionState _connectionState;
    private bool _disposed;

    protected RedisCacheBase(
        IRedisConnector redis,
        ICachingTelemetryProvider telemetryProvider,
        RedisCacheOptions redisCacheOptions,
        CacheOptions cacheOptions,
        ICachePolicyFactory policyFactory)
    {
        _redis = redis;
        Telemetry = telemetryProvider;
        var monitorConnection = redisCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled;
        _connectionState = monitorConnection ? redis : NullConnectionStateMonitor.Instance;
        DefaultPolicy = CachePolicyMerger.Merge(
            new CachePolicy { DistributedExpiration = redisCacheOptions.DefaultExpiration },
            policyFactory.Default);
        DefaultExpiration = DefaultPolicy.DistributedExpiration;
        Clock = new CacheClock(redisCacheOptions.Clock, DefaultExpiration);
        KeyReadTelemetryEnabled = redisCacheOptions.KeyReadTelemetryEnabled;
        RefreshFlags = redisCacheOptions.AwaitRefresh
            ? CommandFlags.DemandMaster
            : CommandFlags.DemandMaster | CommandFlags.FireAndForget;
    }

    protected ICachingTelemetryProvider Telemetry { get; }

    protected bool KeyReadTelemetryEnabled { get; }

    /// <summary>Flags for a standalone TTL write, shared by both caches so the option cannot be honored in one and not the other.</summary>
    internal CommandFlags RefreshFlags { get; }

    protected void TrackRead(ITelemetryOperation operation, bool hit, RedisKey key)
    {
        operation.Track(hit, 1);
        if (KeyReadTelemetryEnabled)
        {
            operation.TrackKeyReads([(key.ToString(), hit)]);
        }
    }

    protected CachePolicy DefaultPolicy { get; }

    protected TimeSpan? DefaultExpiration { get; }

    protected CacheClock Clock { get; }

    /// <summary>
    /// Write duration for a call that carried no <c>expiration</c>: the policy's L2 TTL, then the
    /// cache default, then <see cref="TimeSpan.MaxValue"/> for "no TTL".
    /// </summary>
    protected TimeSpan PolicyDuration(CachePolicy? policy) =>
        Clock.ToTimeSpan(policy?.DistributedExpiration ?? DefaultExpiration);

    /// <summary>
    /// Write deadline for a call that carried no <c>expiration</c>, resolved the same way as
    /// <see cref="PolicyDuration"/> and yielding <see cref="DateTimeOffset.MaxValue"/> for "no TTL".
    /// </summary>
    protected DateTimeOffset PolicyDeadline(CachePolicy? policy) =>
        Clock.ToDateTimeOffset(policy?.DistributedExpiration ?? DefaultExpiration);

    /// <summary>
    /// Write deadline carried by an entry-options object. <see cref="HashCacheEntryOptions"/> keeps
    /// its lifetime fields nullable — an options object is the one seam where <c>null</c> still
    /// means "inherit" — so this resolves <c>ExpireTime</c>, then <c>TimeToLive</c>, then the policy
    /// and cache defaults.
    /// </summary>
    protected DateTimeOffset OptionsDeadline(DateTimeOffset? expireTime, TimeSpan? timeToLive, CachePolicy? policy) =>
        expireTime.HasValue
            ? Clock.ToDateTimeOffset(expireTime)
            : Clock.ToDateTimeOffset(timeToLive ?? policy?.DistributedExpiration ?? DefaultExpiration);

    /// <summary>Validates a caller-supplied duration.</summary>
    protected static TimeSpan CallerDuration(TimeSpan expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        CacheExpiration.ThrowIfNotPositive(expiration, paramName);

    /// <summary>Validates a caller-supplied deadline and turns it into a duration from the cache's now.</summary>
    protected TimeSpan CallerDuration(DateTimeOffset expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        CacheExpiration.ToDuration(expiration, Clock.UtcNow, paramName);

    /// <summary>Validates a caller-supplied deadline.</summary>
    protected DateTimeOffset CallerDeadline(DateTimeOffset expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        CacheExpiration.ThrowIfNotFuture(expiration, Clock.UtcNow, paramName);

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

    protected IDatabase Database => _redis.Database;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

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
}
