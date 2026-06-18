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
    }

    protected ICachingTelemetryProvider Telemetry { get; }

    protected CachePolicy DefaultPolicy { get; }

    protected TimeSpan? DefaultExpiration { get; }

    protected CacheClock Clock { get; }

    protected TimeSpan? ResolveExpiration(TimeSpan? expiration, CachePolicy? policy) =>
        expiration ?? policy?.DistributedExpiration ?? DefaultExpiration;

    protected DateTimeOffset ResolveExpiration(DateTimeOffset? expiration, CachePolicy? policy) =>
        expiration ?? Clock.ToDateTimeOffset(policy?.DistributedExpiration ?? DefaultExpiration);

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
