using System.Collections.Concurrent;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

public abstract class RedisTopicProviderBase(
    IRedisConnector redis,
    ICachingTelemetryProvider telemetryProvider,
    IRedisProfiler redisProfiler,
    ILoggerFactory loggerFactory,
    bool connectionMonitorEnabled)
    : ITopicProvider, IConnectionState
{
    private bool _disposed;

    private readonly ConcurrentDictionary<TopicKey, Lazy<ITopic<ICacheEvent>>> _topics = new();

    private readonly CancellationTokenSource _stopTokenSource = new();

    protected IRedisConnector Redis { get; } = redis;

    protected IConnectionState ConnectionState { get; } = connectionMonitorEnabled ? redis : NullConnectionStateMonitor.Instance;

    protected ICachingTelemetryProvider Telemetry { get; } = telemetryProvider;

    protected IRedisProfiler Profiler { get; } = redisProfiler;

    protected ILoggerFactory LoggerFactory { get; } = loggerFactory;

    public event EventHandler? OnConnectionFailed
    {
        add => ConnectionState.OnConnectionFailed += value;
        remove => ConnectionState.OnConnectionFailed -= value;
    }

    public event EventHandler? OnConnectionRestored
    {
        add => ConnectionState.OnConnectionRestored += value;
        remove => ConnectionState.OnConnectionRestored -= value;
    }

    public event EventHandler? OnReconnected
    {
        add => ConnectionState.OnReconnected += value;
        remove => ConnectionState.OnReconnected -= value;
    }

    public bool IsConnected => ConnectionState.IsConnected;

    public abstract string Name { get; }

    public abstract bool Enabled { get; }

    public ICollection<TopicKey> Keys => _topics.Keys;

    public ITopic<ICacheEvent> Create(TopicKey topicKey) =>
        _topics.GetOrAdd(topicKey, tk => new Lazy<ITopic<ICacheEvent>>(() => {
            var t = CreateInternalTopic(tk);
            t.OnDisposed += RemoveTopic;
            return t;
        })).Value;

    public void Remove(TopicKey topicKey)
    {
        if (_topics.TryRemove(topicKey, out var lazyTopic) && lazyTopic.IsValueCreated)
        {
            var topic = lazyTopic.Value;
            topic.OnDisposed -= RemoveTopic;
            topic.Dispose();
        }
    }

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
                _stopTokenSource?.Cancel();
                _stopTokenSource?.Dispose();
                _topics.Clear();
            }
            _disposed = true;
        }
    }

    private void RemoveTopic(object? sender, EventArgs e)
    {
        if (sender is ITopic<ICacheEvent> topic)
        {
            Remove(topic.TopicKey);
        }
    }

    protected abstract ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey);
}
